/* JPEG decoding through libjpeg's scanline API, which, unlike the one-call TurboJPEG API, can be
 * abandoned part-way: the viewer drops a decode the moment the user has moved on to another image.
 * Built as a static library for the Native AOT executable and as fivjpeg.dll for JIT-run projects;
 * both take libjpeg from turbojpeg-static.lib.
 */
#include <setjmp.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>

#include "jpeglib.h"

#ifdef FIVJPEG_DLL
#define FIV_API __declspec(dllexport)
#else
#define FIV_API
#endif

#define FIV_OK 0
#define FIV_ERROR 1
#define FIV_CANCELLED 2

typedef struct
{
    struct jpeg_error_mgr pub;
    jmp_buf jump;
    char message[JMSG_LENGTH_MAX];
} fiv_error_mgr;

typedef struct
{
    struct jpeg_progress_mgr pub;
    const volatile int *cancel;
} fiv_progress_mgr;

static void fiv_error_exit(j_common_ptr cinfo)
{
    fiv_error_mgr *error = (fiv_error_mgr *)cinfo->err;
    (*cinfo->err->format_message)(cinfo, error->message);
    longjmp(error->jump, FIV_ERROR);
}

/* Corrupt-data warnings: decode what is there, as every viewer does. */
static void fiv_emit_message(j_common_ptr cinfo, int level)
{
    (void)cinfo;
    (void)level;
}

/* libjpeg calls this between rows of MCUs, also while it reads the scans of a progressive file,
 * which is where such a file spends most of its time. */
static void fiv_progress(j_common_ptr cinfo)
{
    fiv_progress_mgr *progress = (fiv_progress_mgr *)cinfo->progress;
    if (progress->cancel != NULL && *progress->cancel != 0)
        longjmp(((fiv_error_mgr *)cinfo->err)->jump, FIV_CANCELLED);
}

/* Decodes to BGRA (4 bytes per pixel) or, with gray != 0, to 8-bit grey, at 1/denominator of the
 * file's size (1, 2, 4 or 8). width and height are the size the caller allocated for; a mismatch
 * is an error. *cancel is polled; set it non-zero from another thread to stop.
 * Returns FIV_OK, FIV_ERROR (message filled) or FIV_CANCELLED. dst is undefined unless FIV_OK.
 */
FIV_API int fiv_jpeg_decode(const unsigned char *data, size_t size, int denominator, int gray, int fast_upsample,
                            unsigned char *dst, int pitch, int width, int height, const volatile int *cancel,
                            char *message, int message_size)
{
    struct jpeg_decompress_struct cinfo;
    fiv_error_mgr error;
    fiv_progress_mgr progress;
    int code;

    cinfo.err = jpeg_std_error(&error.pub);
    error.pub.error_exit = fiv_error_exit;
    error.pub.emit_message = fiv_emit_message;
    error.message[0] = 0;

    code = setjmp(error.jump);
    if (code != 0)
    {
        jpeg_destroy_decompress(&cinfo);
        if (code == FIV_ERROR && message != NULL && message_size > 0)
        {
            strncpy(message, error.message, (size_t)message_size - 1);
            message[message_size - 1] = 0;
        }
        return code;
    }

    jpeg_create_decompress(&cinfo);
    progress.pub.progress_monitor = fiv_progress;
    progress.cancel = cancel;
    cinfo.progress = &progress.pub;

    jpeg_mem_src(&cinfo, data, (unsigned long)size);
    jpeg_read_header(&cinfo, TRUE);
    cinfo.scale_num = 1;
    cinfo.scale_denom = (unsigned int)denominator;
    cinfo.out_color_space = gray ? JCS_GRAYSCALE : JCS_EXT_BGRA;
    if (fast_upsample) cinfo.do_fancy_upsampling = FALSE;
    jpeg_start_decompress(&cinfo);

    if ((int)cinfo.output_width != width || (int)cinfo.output_height != height)
    {
        snprintf(error.message, sizeof(error.message), "decoded size %ux%u differs from the expected %dx%d",
                 cinfo.output_width, cinfo.output_height, width, height);
        longjmp(error.jump, FIV_ERROR);
    }

    while (cinfo.output_scanline < cinfo.output_height)
    {
        JSAMPROW rows[16];
        unsigned int count = cinfo.output_height - cinfo.output_scanline, i;
        if (count > 16) count = 16;
        for (i = 0; i < count; i++) rows[i] = dst + (size_t)(cinfo.output_scanline + i) * (size_t)pitch;
        jpeg_read_scanlines(&cinfo, rows, count);
    }

    jpeg_finish_decompress(&cinfo);
    jpeg_destroy_decompress(&cinfo);
    return FIV_OK;
}
