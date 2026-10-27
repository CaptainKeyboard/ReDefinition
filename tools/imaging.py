"""The two ways the analysis scripts turn colour into one number per pixel, in one place.

A script reaches it like the other shared modules of tools/ (fg_dump.py):

    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import imaging  # noqa: E402

Both take an array of shape (h, w, channels) and return one of shape (h, w): luminance in the precision the array's
floats have (float32 in, float32 out), grey in float32.
"""
import numpy as np


def luminance(rgb):
    """Rec. 709 luminance of RGB (linear light where the caller means luminance in the physical sense): the first three
    channels weighted 0.2126, 0.7152 and 0.0722."""
    return rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722


def grey(image):
    """An image as grey, the mean of its first three channels (RGB; the alpha of an RGBA image is left out), as float32."""
    return image[..., :3].astype(np.float32).mean(axis=2)
