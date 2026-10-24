/* Decoders for the formats the viewer does not leave to the Windows codecs, which are installed on
 * some machines and not on others: HEIF/HEIC and AVIF (libheif with libde265 and dav1d), WebP
 * (libwebp), JPEG XL (libjxl) and camera raw (LibRaw); and libtiff for the TIFFs the Windows
 * codec does not read. Built as fivcodecs.dll; the libraries are linked in statically.
 *
 * A decode is one call. The decoder works out the size and layout of the result, asks the caller
 * for the memory through a callback, and writes the pixels straight into it in one of the layouts
 * the viewer keeps (fiv_layout, numbered as PixelLayout in C#): no second copy of the image.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include <memory>
#include <thread>

#include <jxl/cms.h>
#include <jxl/decode.h>
#include <jxl/thread_parallel_runner.h>
#include <libheif/heif.h>
#include <libheif/heif_sequences.h>
#include <libheif/heif_tiling.h>
#include <libraw/libraw.h>
#include <tiffio.h>
#include <webp/decode.h>
#include <webp/demux.h>

#define FIV_API extern "C" __declspec(dllexport)

enum fiv_result
{
    FIV_OK = 0,
    FIV_ERROR = 1,
    FIV_CANCELLED = 2,
};

enum fiv_format
{
    FIV_FORMAT_NONE = 0,
    FIV_FORMAT_HEIF = 1, /* HEIC, HEIF, AVIF */
    FIV_FORMAT_WEBP = 2,
    FIV_FORMAT_JXL = 3,
    FIV_FORMAT_RAW = 4,
    FIV_FORMAT_TIFF = 5, /* the TIFFs WIC does not read, or reads wrongly; the others are WIC's */
};

enum fiv_layout
{
    FIV_BGRA8 = 0,
    FIV_GRAY8 = 1,
    FIV_GRAY16 = 2,
    FIV_GRAYFLOAT = 3,
    FIV_RGBA16 = 4,
};

static int bytes_per_pixel(int layout)
{
    switch (layout)
    {
    case FIV_GRAY8: return 1;
    case FIV_GRAY16: return 2;
    case FIV_RGBA16: return 8;
    default: return 4;
    }
}

/* Returns memory for width x height pixels of the layout, rows packed without padding, or NULL.
 * source_width and source_height are the size of the file's image, larger than width and height
 * when a smaller picture stored in the file was decoded instead. */
typedef unsigned char *(*fiv_allocator)(void *context, int width, int height, int layout, int source_width, int source_height);

/* What fiv_probe finds out without decoding. */
typedef struct
{
    int format;
    /* Raw only: the size of the decoded image, and the EXIF orientation its pixels still need
     * (raws are decoded as the sensor has them, so that the embedded preview, which is stored the
     * same way, can stand in for the image until it is decoded). 0 and 1 for the other formats. */
    int width, height, orientation;
    /* Raw only: the largest JPEG preview embedded in the file, as a range of the file's bytes; 0 if none. */
    int64_t preview_offset;
    int preview_length;
} fiv_info;

struct fiv_call
{
    fiv_allocator allocate;
    void *context;
    const volatile int *cancel;
    char *message;
    int message_size;

    bool cancelled() const { return cancel != nullptr && *cancel != 0; }

    int fail(const char *format, const char *detail = "")
    {
        if (message != nullptr && message_size > 0) _snprintf_s(message, message_size, _TRUNCATE, format, detail);
        return FIV_ERROR;
    }
};

static uint32_t be32(const unsigned char *p) { return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3]; }

/* A sample of `bits` significant bits (9 to 16) spread over the full 16-bit range, as WIC does it. */
static inline uint16_t widen(uint16_t value, int bits) { return (uint16_t)(value << (16 - bits) | value >> (2 * bits - 16)); }

static void fit_within(int width, int height, int size, int *out_width, int *out_height)
{
    if (width <= size && height <= size)
    {
        *out_width = width;
        *out_height = height;
    }
    else if (width >= height)
    {
        *out_width = size;
        *out_height = (int)((int64_t)height * size / width) > 0 ? (int)((int64_t)height * size / width) : 1;
    }
    else
    {
        *out_height = size;
        *out_width = (int)((int64_t)width * size / height) > 0 ? (int)((int64_t)width * size / height) : 1;
    }
}

/* ---------------------------------------------------------------- identifying */

/* ISO base media files: HEIF and AVIF by their brands, and Canon's CR3, which is one too. */
static int identify_ftyp(const unsigned char *data, size_t size)
{
    if (size < 16 || memcmp(data + 4, "ftyp", 4) != 0) return FIV_FORMAT_NONE;
    size_t box = be32(data);
    if (box < 16 || box > size) box = size < 64 ? size : 64;
    static const char *const heif_brands[] = {"heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs",
                                              "mif1", "mif2", "msf1", "avif", "avis", "avio", "miaf"};
    int format = FIV_FORMAT_NONE;
    for (size_t at = 8; at + 4 <= box; at += 4)
    {
        if (at == 12) continue; /* minor version */
        if (memcmp(data + at, "crx ", 4) == 0) return FIV_FORMAT_RAW;
        for (const char *brand : heif_brands)
            if (memcmp(data + at, brand, 4) == 0) format = FIV_FORMAT_HEIF;
    }
    return format;
}

/* Whether the first directory of a TIFF file has a Make or a DNGVersion tag: TIFF files from
 * cameras have, and only those are offered to LibRaw. Scans and the like go to WIC. */
static bool tiff_from_camera(const unsigned char *data, size_t size)
{
    bool little = data[0] == 'I';
    auto u16 = [&](size_t at) { return little ? (unsigned)(data[at] | data[at + 1] << 8) : (unsigned)(data[at] << 8 | data[at + 1]); };
    auto u32 = [&](size_t at) { return little ? (uint32_t)(u16(at) | u16(at + 2) << 16) : (uint32_t)(u16(at) << 16 | u16(at + 2)); };
    size_t directory = u32(4);
    if (directory + 2 > size) return false;
    unsigned count = u16(directory);
    for (unsigned i = 0; i < count && directory + 2 + (i + 1) * 12 <= size; i++)
    {
        unsigned tag = u16(directory + 2 + i * 12);
        if (tag == 0x010F || tag == 0xC612) return true;
    }
    return false;
}

static int identify(const unsigned char *data, size_t size)
{
    if (size < 16) return FIV_FORMAT_NONE;
    int format = identify_ftyp(data, size);
    if (format != FIV_FORMAT_NONE) return format;
    if (memcmp(data, "RIFF", 4) == 0 && memcmp(data + 8, "WEBP", 4) == 0) return FIV_FORMAT_WEBP;
    JxlSignature jxl = JxlSignatureCheck(data, size);
    if (jxl == JXL_SIG_CODESTREAM || jxl == JXL_SIG_CONTAINER) return FIV_FORMAT_JXL;
    if (memcmp(data, "FUJIFILMCCD-RAW", 15) == 0 || memcmp(data, "IIRO", 4) == 0 || memcmp(data, "IIRS", 4) == 0 ||
        memcmp(data, "MMOR", 4) == 0 || memcmp(data, "IIU\0", 4) == 0 || memcmp(data, "FOVb", 4) == 0 ||
        memcmp(data, "\0MRM", 4) == 0 || memcmp(data + 6, "HEAPCCDR", 8) == 0)
        return FIV_FORMAT_RAW;
    bool tiff = memcmp(data, "II*\0", 4) == 0 || memcmp(data, "MM\0*", 4) == 0;
    if (tiff && (memcmp(data + 8, "CR", 2) == 0 || tiff_from_camera(data, size))) return FIV_FORMAT_RAW;
    return FIV_FORMAT_NONE;
}

/* EXIF orientation from LibRaw's flip, the inverse of dcraw's "50132467"[orientation]. */
static int flip_to_orientation(int flip)
{
    static const int orientation[8] = {1, 2, 4, 3, 5, 8, 6, 7};
    return flip >= 0 && flip < 8 ? orientation[flip] : 1;
}

static bool tiff_for_libtiff(const unsigned char *data, size_t size);
static bool probe_raw(const unsigned char *data, size_t size, fiv_info *info);

/* Which decoder takes the file, and for a raw what is known about it before decoding. Always
 * returns FIV_OK; FIV_FORMAT_NONE means WIC's, also for a camera's TIFF that LibRaw does not take. */
FIV_API int fiv_probe(const unsigned char *data, size_t size, fiv_info *info)
{
    memset(info, 0, sizeof *info);
    info->orientation = 1;
    try
    {
        info->format = identify(data, size);
        if (info->format == FIV_FORMAT_RAW && !probe_raw(data, size, info)) info->format = FIV_FORMAT_NONE;
        bool tiff = size >= 8 && (memcmp(data, "II*\0", 4) == 0 || memcmp(data, "MM\0*", 4) == 0);
        if (info->format == FIV_FORMAT_NONE && tiff && tiff_for_libtiff(data, size)) info->format = FIV_FORMAT_TIFF;
    }
    catch (...)
    {
        memset(info, 0, sizeof *info);
        info->orientation = 1;
    }
    return FIV_OK;
}

/* A raw's size and orientation, and the largest JPEG embedded in it. False if LibRaw does not take the file. */
static bool probe_raw(const unsigned char *data, size_t size, fiv_info *info)
{
    std::unique_ptr<LibRaw> raw(new (std::nothrow) LibRaw(LIBRAW_OPTIONS_NONE));
    if (!raw || raw->open_buffer(data, size) != LIBRAW_SUCCESS) return false;
    const libraw_image_sizes_t &sizes = raw->imgdata.sizes;
    info->width = sizes.width;
    info->height = sizes.height;
    info->orientation = flip_to_orientation(sizes.flip);
    const libraw_thumbnail_list_t &thumbs = raw->imgdata.thumbs_list;
    int64_t best = 0;
    for (int i = 0; i < thumbs.thumbcount && i < LIBRAW_THUMBNAIL_MAXCOUNT; i++)
    {
        const libraw_thumbnail_item_t &thumb = thumbs.thumblist[i];
        if (thumb.tformat != LIBRAW_INTERNAL_THUMBNAIL_JPEG || thumb.toffset < 0 || thumb.tlength < 4) continue;
        if ((uint64_t)thumb.toffset + thumb.tlength > size) continue;
        const unsigned char *jpeg = data + thumb.toffset;
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) continue;
        int64_t area = (int64_t)thumb.twidth * thumb.theight;
        if (area == 0) area = thumb.tlength; /* size unknown: the longest is the best guess */
        if (area > best)
        {
            best = area;
            info->preview_offset = thumb.toffset;
            info->preview_length = (int)thumb.tlength;
        }
    }
    return true;
}

/* ---------------------------------------------------------------- HEIF, HEIC, AVIF */

static INIT_ONCE heif_once = INIT_ONCE_STATIC_INIT;

static BOOL CALLBACK heif_initialize(PINIT_ONCE, PVOID, PVOID *)
{
    heif_init(nullptr);
    return TRUE;
}

static int heif_cancel(void *cancel) { return cancel != nullptr && *(const volatile int *)cancel != 0; }

struct heif_resources
{
    heif_context *context = nullptr;
    heif_image_handle *primary = nullptr;
    heif_image_handle *thumbnail = nullptr;
    heif_track *track = nullptr;
    heif_image *image = nullptr;
    heif_decoding_options *options = nullptr;

    ~heif_resources()
    {
        heif_image_release(image);
        heif_decoding_options_free(options);
        heif_track_release(track);
        heif_image_handle_release(thumbnail);
        heif_image_handle_release(primary);
        heif_context_free(context);
    }
};

/* The smallest thumbnail whose long side reaches `size`, or NULL. */
static heif_image_handle *heif_pick_thumbnail(heif_image_handle *primary, int size)
{
    int count = heif_image_handle_get_number_of_thumbnails(primary);
    if (count <= 0) return nullptr;
    heif_item_id ids[16];
    count = heif_image_handle_get_list_of_thumbnail_IDs(primary, ids, count < 16 ? count : 16);
    heif_image_handle *best = nullptr;
    int best_long = 0;
    for (int i = 0; i < count; i++)
    {
        heif_image_handle *thumbnail = nullptr;
        if (heif_image_handle_get_thumbnail(primary, ids[i], &thumbnail).code != heif_error_Ok) continue;
        int width = heif_image_handle_get_width(thumbnail), height = heif_image_handle_get_height(thumbnail);
        int long_side = width > height ? width : height;
        if (long_side >= size && (best == nullptr || long_side < best_long))
        {
            heif_image_handle_release(best);
            best = thumbnail;
            best_long = long_side;
        }
        else
        {
            heif_image_handle_release(thumbnail);
        }
    }
    return best;
}

static int decode_heif(const unsigned char *data, size_t size, int hint, fiv_call &call)
{
    InitOnceExecuteOnce(&heif_once, heif_initialize, nullptr, nullptr);
    heif_resources r;
    r.context = heif_context_alloc();
    heif_error error = heif_context_read_from_memory_without_copy(r.context, data, size, nullptr);
    if (error.code != heif_error_Ok) return call.fail("%s", error.message);

    int source_width = 0, source_height = 0, bits = 8;
    bool alpha = false, gray = false;
    if (heif_context_get_primary_image_handle(r.context, &r.primary).code == heif_error_Ok)
    {
        // Sizes after the file's rotation, mirroring and cropping, which libheif applies.
        source_width = heif_image_handle_get_width(r.primary);
        source_height = heif_image_handle_get_height(r.primary);
        heif_image_handle *handle = r.primary;
        if (hint > 0 && (r.thumbnail = heif_pick_thumbnail(r.primary, hint)) != nullptr) handle = r.thumbnail;
        alpha = heif_image_handle_has_alpha_channel(handle) != 0;
        int luma = heif_image_handle_get_luma_bits_per_pixel(handle);
        bits = luma > 8 ? luma : 8;
        heif_colorspace colorspace;
        heif_chroma chroma;
        if (heif_image_handle_get_preferred_decoding_colorspace(handle, &colorspace, &chroma).code == heif_error_Ok)
            gray = colorspace == heif_colorspace_monochrome;
    }
    else if (heif_context_has_sequence(r.context) && (r.track = heif_context_get_track(r.context, 0)) != nullptr)
    {
        // A sequence without a still image: its first picture, in 8 bits.
        alpha = heif_track_has_alpha_channel(r.track) != 0;
    }
    else
    {
        return call.fail("The file has no image.");
    }

    int layout = gray && !alpha ? (bits > 8 ? FIV_GRAY16 : FIV_GRAY8) : (bits > 8 ? FIV_RGBA16 : FIV_BGRA8);
    heif_colorspace colorspace = layout == FIV_GRAY8 || layout == FIV_GRAY16 ? heif_colorspace_monochrome : heif_colorspace_RGB;
    heif_chroma chroma = layout == FIV_GRAY8 || layout == FIV_GRAY16 ? heif_chroma_monochrome
                         : layout == FIV_RGBA16                     ? heif_chroma_interleaved_RRGGBBAA_LE
                                                                    : heif_chroma_interleaved_RGBA;
    r.options = heif_decoding_options_alloc();
    r.options->cancel_decoding = heif_cancel;
    r.options->progress_user_data = (void *)call.cancel;
    r.options->convert_hdr_to_8bit = layout == FIV_BGRA8 || layout == FIV_GRAY8;
    // Every core on the picture: a grid (as phones store their photos) by decoding its tiles side
    // by side, one thread each; a single coded image by the codec's own threads. libheif's
    // defaults are 4 tiles at a time and, for HEVC, one thread.
    int cores = (int)std::thread::hardware_concurrency();
    if (cores < 1) cores = 1;
    heif_image_tiling tiling = {};
    heif_image_handle *decoded = r.thumbnail != nullptr ? r.thumbnail : r.primary;
    bool grid = decoded != nullptr && heif_image_handle_get_image_tiling(decoded, 1, &tiling).code == heif_error_Ok &&
                tiling.num_columns * tiling.num_rows > 1;
    heif_context_set_max_decoding_threads(r.context, grid ? cores : 1);
    r.options->num_codec_threads = grid ? 1 : cores;

    error = r.track != nullptr ? heif_track_decode_next_image(r.track, &r.image, colorspace, chroma, r.options)
                               : heif_decode_image(r.thumbnail != nullptr ? r.thumbnail : r.primary, &r.image, colorspace, chroma, r.options);
    if (call.cancelled() || error.code == heif_error_Canceled) return FIV_CANCELLED;
    if (error.code != heif_error_Ok) return call.fail("%s", error.message);

    heif_channel channel = colorspace == heif_colorspace_monochrome ? heif_channel_Y : heif_channel_interleaved;
    int width = heif_image_get_width(r.image, channel), height = heif_image_get_height(r.image, channel);
    size_t stride = 0;
    const uint8_t *plane = heif_image_get_plane_readonly2(r.image, channel, &stride);
    if (plane == nullptr || width <= 0 || height <= 0) return call.fail("The decoded image has no pixels.");
    if (source_width <= 0) source_width = width, source_height = height;
    int range = heif_image_get_bits_per_pixel_range(r.image, channel);

    unsigned char *dst = call.allocate(call.context, width, height, layout, source_width, source_height);
    if (dst == nullptr) return call.fail("Out of memory.");
    size_t row_bytes = (size_t)width * bytes_per_pixel(layout);
    for (int y = 0; y < height; y++)
    {
        const uint8_t *in = plane + y * stride;
        unsigned char *out = dst + y * row_bytes;
        if (layout == FIV_BGRA8)
        {
            for (int x = 0; x < width; x++, in += 4, out += 4)
            {
                out[0] = in[2];
                out[1] = in[1];
                out[2] = in[0];
                out[3] = in[3];
            }
        }
        else if (layout == FIV_GRAY8 || range >= 16 || range <= 8)
        {
            memcpy(out, in, row_bytes);
        }
        else
        {
            const uint16_t *in16 = (const uint16_t *)in;
            uint16_t *out16 = (uint16_t *)out;
            for (size_t i = 0; i < row_bytes / 2; i++) out16[i] = widen(in16[i], range);
        }
    }
    return FIV_OK;
}

/* ---------------------------------------------------------------- WebP */

static int decode_webp(const unsigned char *data, size_t size, int hint, fiv_call &call)
{
    WebPBitstreamFeatures features;
    if (WebPGetFeatures(data, size, &features) != VP8_STATUS_OK) return call.fail("Not a WebP file, or its header is damaged.");

    if (features.has_animation)
    {
        // The first frame, composed on the canvas as a browser shows it.
        WebPAnimDecoderOptions options;
        if (!WebPAnimDecoderOptionsInit(&options)) return call.fail("libwebp version mismatch.");
        options.color_mode = MODE_BGRA;
        options.use_threads = 1;
        WebPData webp = {data, size};
        WebPAnimDecoder *decoder = WebPAnimDecoderNew(&webp, &options);
        if (decoder == nullptr) return call.fail("The animation cannot be read.");
        WebPAnimInfo info;
        uint8_t *frame = nullptr;
        int timestamp = 0;
        int result = FIV_OK;
        if (!WebPAnimDecoderGetInfo(decoder, &info) || !WebPAnimDecoderGetNext(decoder, &frame, &timestamp))
        {
            result = call.fail("The first frame of the animation cannot be decoded.");
        }
        else
        {
            int width = (int)info.canvas_width, height = (int)info.canvas_height;
            unsigned char *dst = call.allocate(call.context, width, height, FIV_BGRA8, width, height);
            if (dst == nullptr) result = call.fail("Out of memory.");
            else memcpy(dst, frame, (size_t)width * height * 4);
        }
        WebPAnimDecoderDelete(decoder);
        return result;
    }

    WebPDecoderConfig config;
    if (!WebPInitDecoderConfig(&config)) return call.fail("libwebp version mismatch.");
    int width = features.width, height = features.height;
    if (hint > 0)
    {
        // libwebp scales while it decodes, much cheaper than decoding in full and reducing.
        fit_within(features.width, features.height, hint, &width, &height);
        if (width != features.width || height != features.height)
        {
            config.options.use_scaling = 1;
            config.options.scaled_width = width;
            config.options.scaled_height = height;
        }
    }
    unsigned char *dst = call.allocate(call.context, width, height, FIV_BGRA8, features.width, features.height);
    if (dst == nullptr) return call.fail("Out of memory.");
    config.options.use_threads = 1;
    config.output.colorspace = MODE_BGRA;
    config.output.is_external_memory = 1;
    config.output.u.RGBA.rgba = dst;
    config.output.u.RGBA.stride = width * 4;
    config.output.u.RGBA.size = (size_t)width * 4 * height;
    VP8StatusCode status = WebPDecode(data, size, &config);
    WebPFreeDecBuffer(&config.output);
    return status == VP8_STATUS_OK ? FIV_OK : call.fail("The WebP data is damaged or incomplete.");
}

/* ---------------------------------------------------------------- JPEG XL */

struct jxl_target
{
    unsigned char *dst;
    size_t row_bytes;
};

/* RGBA as libjxl gives it to BGRA. Called from libjxl's worker threads, each with rows of its own. */
static void jxl_store_bgra(void *opaque, size_t x, size_t y, size_t count, const void *pixels)
{
    const jxl_target *target = (const jxl_target *)opaque;
    const uint8_t *in = (const uint8_t *)pixels;
    unsigned char *out = target->dst + y * target->row_bytes + x * 4;
    for (size_t i = 0; i < count; i++, in += 4, out += 4)
    {
        out[0] = in[2];
        out[1] = in[1];
        out[2] = in[0];
        out[3] = in[3];
    }
}

struct jxl_resources
{
    JxlDecoder *decoder = nullptr;
    void *runner = nullptr;

    ~jxl_resources()
    {
        JxlDecoderDestroy(decoder);
        JxlThreadParallelRunnerDestroy(runner);
    }
};

static int decode_jxl(const unsigned char *data, size_t size, fiv_call &call)
{
    jxl_resources r;
    r.decoder = JxlDecoderCreate(nullptr);
    r.runner = JxlThreadParallelRunnerCreate(nullptr, JxlThreadParallelRunnerDefaultNumWorkerThreads());
    if (r.decoder == nullptr || r.runner == nullptr) return call.fail("Out of memory.");
    if (JxlDecoderSetParallelRunner(r.decoder, JxlThreadParallelRunner, r.runner) != JXL_DEC_SUCCESS ||
        JxlDecoderSubscribeEvents(r.decoder, JXL_DEC_BASIC_INFO | JXL_DEC_COLOR_ENCODING | JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS ||
        JxlDecoderSetInput(r.decoder, data, size) != JXL_DEC_SUCCESS)
        return call.fail("libjxl cannot be set up.");
    JxlDecoderCloseInput(r.decoder);
    JxlDecoderSetCms(r.decoder, *JxlGetDefaultCms());
    JxlDecoderSetUnpremultiplyAlpha(r.decoder, JXL_TRUE); // the viewer keeps alpha straight, as every other decoder gives it

    JxlBasicInfo info = {};
    JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
    jxl_target target = {};
    int layout = FIV_BGRA8, width = 0, height = 0;
    for (;;)
    {
        JxlDecoderStatus status = JxlDecoderProcessInput(r.decoder);
        if (call.cancelled()) return FIV_CANCELLED;
        switch (status)
        {
        case JXL_DEC_BASIC_INFO:
        {
            if (JxlDecoderGetBasicInfo(r.decoder, &info) != JXL_DEC_SUCCESS) return call.fail("The JPEG XL header cannot be read.");
            // libjxl turns the image upright as it decodes.
            bool swap = info.orientation >= JXL_ORIENT_TRANSPOSE;
            width = (int)(swap ? info.ysize : info.xsize);
            height = (int)(swap ? info.xsize : info.ysize);
            bool gray = info.num_color_channels == 1 && info.alpha_bits == 0 && info.num_extra_channels == 0;
            bool floating = info.exponent_bits_per_sample > 0;
            if (floating) layout = gray ? FIV_GRAYFLOAT : FIV_RGBA16;
            else if (info.bits_per_sample > 8) layout = gray ? FIV_GRAY16 : FIV_RGBA16;
            else layout = gray ? FIV_GRAY8 : FIV_BGRA8;
            format.num_channels = gray ? 1 : 4;
            format.data_type = layout == FIV_GRAYFLOAT ? JXL_TYPE_FLOAT : layout == FIV_GRAY16 || layout == FIV_RGBA16 ? JXL_TYPE_UINT16 : JXL_TYPE_UINT8;
            target.dst = call.allocate(call.context, width, height, layout, width, height);
            if (target.dst == nullptr) return call.fail("Out of memory.");
            target.row_bytes = (size_t)width * bytes_per_pixel(layout);
            break;
        }
        case JXL_DEC_COLOR_ENCODING:
        {
            // Lossy files are stored in XYB, which has to be turned into some RGB: sRGB, as the
            // screen is taken to be. Lossless ones come out in their own colour space.
            if (!info.uses_original_profile)
            {
                JxlColorEncoding srgb = {};
                srgb.color_space = format.num_channels == 1 ? JXL_COLOR_SPACE_GRAY : JXL_COLOR_SPACE_RGB;
                srgb.white_point = JXL_WHITE_POINT_D65;
                srgb.primaries = JXL_PRIMARIES_SRGB;
                srgb.transfer_function = JXL_TRANSFER_FUNCTION_SRGB;
                srgb.rendering_intent = JXL_RENDERING_INTENT_PERCEPTUAL;
                JxlDecoderSetPreferredColorProfile(r.decoder, &srgb);
            }
            break;
        }
        case JXL_DEC_NEED_IMAGE_OUT_BUFFER:
        {
            JxlDecoderStatus set = layout == FIV_BGRA8
                                       ? JxlDecoderSetImageOutCallback(r.decoder, &format, jxl_store_bgra, &target)
                                       : JxlDecoderSetImageOutBuffer(r.decoder, &format, target.dst, target.row_bytes * height);
            if (set != JXL_DEC_SUCCESS) return call.fail("libjxl does not take the output format.");
            break;
        }
        case JXL_DEC_FULL_IMAGE: /* the first frame is all that is shown */
        case JXL_DEC_SUCCESS:
            return target.dst != nullptr ? FIV_OK : call.fail("The file has no image.");
        case JXL_DEC_NEED_MORE_INPUT:
            return call.fail("The file is incomplete.");
        case JXL_DEC_ERROR:
            return call.fail("The JPEG XL data is damaged or not supported.");
        default:
            break;
        }
    }
}

/* ---------------------------------------------------------------- camera raw */

static int raw_progress(void *cancel, enum LibRaw_progress, int, int) { return cancel != nullptr && *(const volatile int *)cancel != 0; }

static int decode_raw(const unsigned char *data, size_t size, int hint, fiv_call &call)
{
    std::unique_ptr<LibRaw> raw(new (std::nothrow) LibRaw(LIBRAW_OPTIONS_NONE));
    if (!raw) return call.fail("Out of memory.");
    raw->set_progress_handler(raw_progress, (void *)call.cancel);
    int result = raw->open_buffer(data, size);
    if (call.cancelled() || result == LIBRAW_CANCELLED_BY_CALLBACK) return FIV_CANCELLED; // the handler is asked while opening, too
    if (result != LIBRAW_SUCCESS) return call.fail("%s", libraw_strerror(result));

    libraw_output_params_t &params = raw->imgdata.params;
    params.output_bps = 16;
    params.use_camera_wb = 1;
    params.user_flip = 0;        /* as stored; the orientation from fiv_probe turns it upright */
    params.gamm[0] = 1 / 2.4;    /* sRGB rather than dcraw's BT.709 curve */
    params.gamm[1] = 12.92;
    params.half_size = hint > 0; /* a quarter of the pixels, without demosaicing: for thumbnails */
    // Bayer sensors get AHD, LibRaw's default. Fujifilm's X-Trans gets Markesteijn's 1-pass
    // instead of 3-pass, as darktable does by default: 1.9 s instead of 3.3 s for 26 MP.
    if (raw->imgdata.idata.filters == 9) params.user_qual = 2;

    result = raw->unpack();
    if (result == LIBRAW_SUCCESS) result = raw->dcraw_process();
    if (call.cancelled() || result == LIBRAW_CANCELLED_BY_CALLBACK) return FIV_CANCELLED;
    if (result != LIBRAW_SUCCESS) return call.fail("%s", libraw_strerror(result));

    int width, height, colors, bps;
    raw->get_mem_image_format(&width, &height, &colors, &bps);
    if (width <= 0 || height <= 0 || bps != 16 || (colors != 1 && colors != 3)) return call.fail("LibRaw gave an unexpected image format.");
    int layout = colors == 1 ? FIV_GRAY16 : FIV_RGBA16;
    unsigned char *dst = call.allocate(call.context, width, height, layout, raw->imgdata.sizes.width, raw->imgdata.sizes.height);
    if (dst == nullptr) return call.fail("Out of memory.");
    // LibRaw writes RGB rows; each goes at the start of its RGBA row and is spread out in place, from the end.
    int stride = width * bytes_per_pixel(layout);
    result = raw->copy_mem_image(dst, stride, 0);
    if (result != LIBRAW_SUCCESS) return call.fail("%s", libraw_strerror(result));
    if (colors == 3)
    {
        for (int y = 0; y < height; y++)
        {
            uint16_t *row = (uint16_t *)(dst + (size_t)y * stride);
            for (int x = width - 1; x >= 0; x--)
            {
                uint16_t red = row[x * 3], green = row[x * 3 + 1], blue = row[x * 3 + 2];
                row[x * 4] = red;
                row[x * 4 + 1] = green;
                row[x * 4 + 2] = blue;
                row[x * 4 + 3] = 0xFFFF;
            }
        }
    }
    return FIV_OK;
}

/* ---------------------------------------------------------------- TIFF, after WIC */

struct tiff_memory
{
    const unsigned char *data;
    size_t size, position;
};

static tmsize_t tiff_read(thandle_t handle, void *buffer, tmsize_t count)
{
    tiff_memory *m = (tiff_memory *)handle;
    size_t left = m->position < m->size ? m->size - m->position : 0;
    size_t n = (size_t)count < left ? (size_t)count : left;
    memcpy(buffer, m->data + m->position, n);
    m->position += n;
    return (tmsize_t)n;
}

static tmsize_t tiff_write(thandle_t, void *, tmsize_t) { return 0; }

static toff_t tiff_seek(thandle_t handle, toff_t offset, int whence)
{
    tiff_memory *m = (tiff_memory *)handle;
    uint64_t base = whence == SEEK_CUR ? m->position : whence == SEEK_END ? m->size : 0;
    m->position = (size_t)(base + offset);
    return m->position;
}

static int tiff_close(thandle_t) { return 0; }
static toff_t tiff_size(thandle_t handle) { return ((tiff_memory *)handle)->size; }

static int tiff_map(thandle_t handle, void **base, toff_t *size)
{
    *base = (void *)((tiff_memory *)handle)->data;
    *size = ((tiff_memory *)handle)->size;
    return 1;
}

static void tiff_unmap(thandle_t, void *, toff_t) {}

/* libtiff's errors become the message; its warnings (unknown tags and the like) are dropped. */
static int tiff_error(TIFF *, void *user, const char *, const char *format, va_list arguments)
{
    fiv_call *call = (fiv_call *)user;
    if (call->message != nullptr && call->message_size > 0 && call->message[0] == 0)
        _vsnprintf_s(call->message, call->message_size, _TRUNCATE, format, arguments);
    return 1;
}

static int tiff_warning(TIFF *, void *, const char *, const char *, va_list) { return 1; }

static float half_to_float(uint16_t h)
{
    uint32_t sign = (uint32_t)(h & 0x8000) << 16, exponent = (h >> 10) & 0x1F, mantissa = h & 0x3FF, bits;
    if (exponent == 0x1F) bits = sign | 0x7F800000 | mantissa << 13;
    else if (exponent != 0) bits = sign | (exponent + 112) << 23 | mantissa << 13;
    else if (mantissa == 0) bits = sign;
    else
    {
        int shift = 0;
        while ((mantissa & 0x400) == 0) mantissa <<= 1, shift++;
        bits = sign | (uint32_t)(113 - shift) << 23 | (mantissa & 0x3FF) << 13;
    }
    float f;
    memcpy(&f, &bits, 4);
    return f;
}

struct tiff_layout
{
    uint32_t width, height;
    uint16_t bits, samples, format, photometric, planar;
    int layout;
};

/* One sample of a decoded strip or tile row, as a fraction of full scale (integers) or as stored (floats). */
static double tiff_sample(const tiff_layout &t, const unsigned char *row, uint32_t index)
{
    if (t.format == SAMPLEFORMAT_IEEEFP)
    {
        if (t.bits == 16) return half_to_float(((const uint16_t *)row)[index]);
        if (t.bits == 64) return ((const double *)row)[index];
        return ((const float *)row)[index];
    }
    uint64_t value;
    if (t.bits == 8) value = row[index];
    else if (t.bits == 16) value = ((const uint16_t *)row)[index];
    else if (t.bits == 32) value = ((const uint32_t *)row)[index];
    else if (t.bits == 24) value = row[index * 3] | (uint32_t)row[index * 3 + 1] << 8 | (uint32_t)row[index * 3 + 2] << 16;
    else
    {
        // Other widths are packed most significant bit first, whatever the byte order.
        uint64_t bit = (uint64_t)index * t.bits;
        value = 0;
        for (int i = 0; i < t.bits; i++, bit++) value = value << 1 | ((row[bit >> 3] >> (7 - (bit & 7))) & 1);
    }
    if (t.format == SAMPLEFORMAT_INT) value ^= 1ull << (t.bits - 1); // two's complement: 0 goes to the middle of the range
    // To 16 bits as the other decoders do it (fewer bits repeated into the low ones, more cut), so
    // that the fraction returned is exactly a 16-bit value.
    uint32_t v16 = t.bits >= 16 ? (uint32_t)(value >> (t.bits - 16))
                 : t.bits >= 8  ? widen((uint16_t)value, t.bits)
                                : (uint32_t)(value * 65535 / ((1u << t.bits) - 1));
    return v16 / 65535.0;
}

static void tiff_store(const tiff_layout &t, unsigned char *dst, uint32_t x, uint32_t y, int channel, double value)
{
    size_t pixel = (size_t)y * t.width + x;
    if (t.layout == FIV_GRAYFLOAT)
    {
        if (channel == 0) ((float *)dst)[pixel] = (float)(t.photometric == PHOTOMETRIC_MINISWHITE ? 1.0 - value : value);
        return;
    }
    if (t.photometric == PHOTOMETRIC_MINISWHITE && channel < 3) value = 1.0 - value;
    value = value < 0 ? 0 : value > 1 ? 1 : value;
    uint16_t v = (uint16_t)(value * 65535.0 + 0.5);
    if (t.layout == FIV_GRAY16)
    {
        if (channel == 0) ((uint16_t *)dst)[pixel] = v;
        return;
    }
    uint16_t *out = (uint16_t *)dst + pixel * 4;
    bool gray = t.photometric == PHOTOMETRIC_MINISBLACK || t.photometric == PHOTOMETRIC_MINISWHITE;
    if (gray && channel == 0) out[0] = out[1] = out[2] = v;
    else if (gray && channel == 1) out[3] = v;
    else if (!gray && channel < 4) out[channel] = v;
}

/* Integers of 9 to 32 bits, signed ones of any size, and floats of 16, 32 or 64 bits, grey or RGB,
 * chunky or planar, in strips or tiles: the sample formats WIC leaves out or gets wrong. */
static int tiff_decode_samples(TIFF *tif, tiff_layout &t, fiv_call &call)
{
    bool gray = t.photometric == PHOTOMETRIC_MINISBLACK || t.photometric == PHOTOMETRIC_MINISWHITE;
    if (t.format == SAMPLEFORMAT_IEEEFP) t.layout = gray && t.samples == 1 ? FIV_GRAYFLOAT : FIV_RGBA16;
    else t.layout = gray && t.samples == 1 ? FIV_GRAY16 : FIV_RGBA16;
    unsigned char *dst = call.allocate(call.context, (int)t.width, (int)t.height, t.layout, (int)t.width, (int)t.height);
    if (dst == nullptr) return call.fail("Out of memory.");
    if (t.layout == FIV_RGBA16)
        for (size_t i = 0; i < (size_t)t.width * t.height; i++) ((uint16_t *)dst)[i * 4 + 3] = 0xFFFF;

    bool planar = t.planar == PLANARCONFIG_SEPARATE && t.samples > 1;
    int planes = planar ? t.samples : 1, per_pixel = planar ? 1 : t.samples;
    bool tiled = TIFFIsTiled(tif) != 0;
    uint32_t block_width = t.width, block_height = 0;
    if (tiled)
    {
        TIFFGetField(tif, TIFFTAG_TILEWIDTH, &block_width);
        TIFFGetField(tif, TIFFTAG_TILELENGTH, &block_height);
    }
    else if (!TIFFGetFieldDefaulted(tif, TIFFTAG_ROWSPERSTRIP, &block_height) || block_height > t.height)
    {
        block_height = t.height;
    }
    if (block_width == 0 || block_height == 0) return call.fail("The TIFF has no strips or tiles.");
    tmsize_t block_size = tiled ? TIFFTileSize(tif) : TIFFStripSize(tif);
    size_t row_bytes = ((size_t)block_width * per_pixel * t.bits + 7) / 8;
    if (block_size <= 0 || (size_t)block_size < row_bytes) return call.fail("The TIFF's strips or tiles have no size.");
    std::unique_ptr<unsigned char[]> block(new unsigned char[(size_t)block_size]);

    for (int plane = 0; plane < planes; plane++)
    {
        for (uint32_t y0 = 0; y0 < t.height; y0 += block_height)
        {
            for (uint32_t x0 = 0; x0 < t.width; x0 += block_width)
            {
                if (call.cancelled()) return FIV_CANCELLED;
                tmsize_t got = tiled ? TIFFReadEncodedTile(tif, TIFFComputeTile(tif, x0, y0, 0, (uint16_t)plane), block.get(), block_size)
                                     : TIFFReadEncodedStrip(tif, TIFFComputeStrip(tif, y0, (uint16_t)plane), block.get(), block_size);
                if (got < 0) return call.fail("A strip or tile of the TIFF cannot be read.");
                uint32_t rows = block_height < t.height - y0 ? block_height : t.height - y0;
                uint32_t columns = block_width < t.width - x0 ? block_width : t.width - x0;
                for (uint32_t r = 0; r < rows && (size_t)(r + 1) * row_bytes <= (size_t)got; r++)
                {
                    const unsigned char *row = block.get() + r * row_bytes;
                    for (uint32_t c = 0; c < columns; c++)
                        for (int s = 0; s < per_pixel; s++)
                            tiff_store(t, dst, x0 + c, y0 + r, planar ? plane : s, tiff_sample(t, row, c * per_pixel + s));
                }
            }
        }
    }
    return FIV_OK;
}

static TIFF *tiff_open(tiff_memory &memory, fiv_call *call)
{
    TIFFOpenOptions *options = TIFFOpenOptionsAlloc();
    TIFFOpenOptionsSetErrorHandlerExtR(options, call != nullptr ? tiff_error : tiff_warning, call);
    TIFFOpenOptionsSetWarningHandlerExtR(options, tiff_warning, nullptr);
    TIFF *tif = TIFFClientOpenExt("memory", "rm", &memory, tiff_read, tiff_write, tiff_seek, tiff_close, tiff_size, tiff_map, tiff_unmap, options);
    TIFFOpenOptionsFree(options);
    return tif;
}

/* The samples WIC fails on (10-, 14-, 24-bit, 64-bit float, LogLuv, uncompressed YCbCr) or reads
 * wrongly without saying so (16-bit float as integers, 32-bit integers, signed integers). */
static bool tiff_for_libtiff(const unsigned char *data, size_t size)
{
    tiff_memory memory = {data, size, 0};
    TIFF *tif = tiff_open(memory, nullptr);
    if (tif == nullptr) return false;
    uint16_t bits = 1, format = SAMPLEFORMAT_UINT, photometric = 0, compression = COMPRESSION_NONE;
    TIFFGetFieldDefaulted(tif, TIFFTAG_BITSPERSAMPLE, &bits);
    TIFFGetFieldDefaulted(tif, TIFFTAG_SAMPLEFORMAT, &format);
    TIFFGetField(tif, TIFFTAG_PHOTOMETRIC, &photometric);
    TIFFGetFieldDefaulted(tif, TIFFTAG_COMPRESSION, &compression);
    TIFFClose(tif);
    if (format == SAMPLEFORMAT_IEEEFP) return bits != 32;
    if (format == SAMPLEFORMAT_INT) return true;
    if (bits != 1 && bits != 2 && bits != 4 && bits != 8 && bits != 16) return true;
    if (photometric == PHOTOMETRIC_LOGLUV || photometric == PHOTOMETRIC_LOGL) return true;
    return photometric == PHOTOMETRIC_YCBCR && compression != COMPRESSION_JPEG && compression != COMPRESSION_OJPEG;
}

static int decode_tiff(const unsigned char *data, size_t size, fiv_call &call)
{
    tiff_memory memory = {data, size, 0};
    TIFF *tif = tiff_open(memory, &call);
    if (tif == nullptr) return call.message != nullptr && call.message[0] != 0 ? FIV_ERROR : call.fail("Not a TIFF file.");
    std::unique_ptr<TIFF, void (*)(TIFF *)> close(tif, TIFFClose);

    tiff_layout t = {};
    TIFFGetField(tif, TIFFTAG_IMAGEWIDTH, &t.width);
    TIFFGetField(tif, TIFFTAG_IMAGELENGTH, &t.height);
    TIFFGetFieldDefaulted(tif, TIFFTAG_BITSPERSAMPLE, &t.bits);
    TIFFGetFieldDefaulted(tif, TIFFTAG_SAMPLESPERPIXEL, &t.samples);
    TIFFGetFieldDefaulted(tif, TIFFTAG_SAMPLEFORMAT, &t.format);
    TIFFGetFieldDefaulted(tif, TIFFTAG_PLANARCONFIG, &t.planar);
    if (!TIFFGetField(tif, TIFFTAG_PHOTOMETRIC, &t.photometric)) t.photometric = t.samples >= 3 ? PHOTOMETRIC_RGB : PHOTOMETRIC_MINISBLACK;
    if (t.width == 0 || t.height == 0 || t.width > 1u << 20 || t.height > 1u << 20) return call.fail("The TIFF has no usable size.");

    bool plain = t.photometric == PHOTOMETRIC_MINISBLACK || t.photometric == PHOTOMETRIC_MINISWHITE ||
                 (t.photometric == PHOTOMETRIC_RGB && t.samples >= 3);
    bool integer = t.format == SAMPLEFORMAT_UINT || t.format == SAMPLEFORMAT_INT;
    bool deep = (integer && (t.bits > 8 || t.format == SAMPLEFORMAT_INT) && t.bits <= 32) ||
                (t.format == SAMPLEFORMAT_IEEEFP && (t.bits == 16 || t.bits == 32 || t.bits == 64));
    if (plain && deep) return tiff_decode_samples(tif, t, call);

    // Everything else (LogLuv, YCbCr, CIE Lab, CMYK, palettes, 1 to 8 bits) through libtiff's RGBA interface, to 8 bits.
    char reason[1024];
    if (!TIFFRGBAImageOK(tif, reason)) return call.fail("%s", reason);
    unsigned char *dst = call.allocate(call.context, (int)t.width, (int)t.height, FIV_BGRA8, (int)t.width, (int)t.height);
    if (dst == nullptr) return call.fail("Out of memory.");
    if (!TIFFReadRGBAImageOriented(tif, t.width, t.height, (uint32_t *)dst, ORIENTATION_TOPLEFT, 0))
        return call.message != nullptr && call.message[0] != 0 ? FIV_ERROR : call.fail("The TIFF cannot be read.");
    for (size_t i = 0; i < (size_t)t.width * t.height; i++)
    {
        unsigned char red = dst[i * 4];
        dst[i * 4] = dst[i * 4 + 2];
        dst[i * 4 + 2] = red;
    }
    return FIV_OK;
}

/* ---------------------------------------------------------------- entry point */

/* Decodes the file as `format` (from fiv_probe). With hint > 0, a smaller picture stored in the
 * file whose long side is at least `hint` may be decoded instead (HEIF thumbnails), or the image
 * may be decoded at a reduced size (WebP, raw); with 0, the image in full. *cancel is polled; set
 * it non-zero from another thread to stop. Returns FIV_OK, FIV_ERROR (message filled) or FIV_CANCELLED.
 */
FIV_API int fiv_decode(const unsigned char *data, size_t size, int format, int hint, fiv_allocator allocate, void *context,
                       const volatile int *cancel, char *message, int message_size)
{
    fiv_call call = {allocate, context, cancel, message, message_size};
    if (message != nullptr && message_size > 0) message[0] = 0;
    try
    {
        switch (format)
        {
        case FIV_FORMAT_HEIF: return decode_heif(data, size, hint, call);
        case FIV_FORMAT_WEBP: return decode_webp(data, size, hint, call);
        case FIV_FORMAT_JXL: return decode_jxl(data, size, call);
        case FIV_FORMAT_RAW: return decode_raw(data, size, hint, call);
        case FIV_FORMAT_TIFF: return decode_tiff(data, size, call);
        default: return call.fail("Not a format of this library.");
        }
    }
    catch (const std::bad_alloc &)
    {
        return call.fail("Out of memory.");
    }
    catch (...)
    {
        return call.fail("The decoder failed.");
    }
}
