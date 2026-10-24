"""Regenerate public synthetic fixtures with Pillow 12.0.0; never needs private media."""
from pathlib import Path
from PIL import Image, ImageCms

root = Path(__file__).with_name('fixtures')
root.mkdir(exist_ok=True)
image = Image.new('RGB', (96, 64))
image.putdata([(x * 255 // 96, y * 255 // 64, (x * 13 + y * 7) % 256)
               for y in range(64) for x in range(96)])
profile = ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
# ICC creation dates are irrelevant to colour, but fixed bytes keep fixtures reproducible.
profile = profile[:24] + bytes.fromhex('07d000010001000000000000') + profile[36:]
for suffix in ['jpg', 'webp']:
    image.save(root / ('colour.' + suffix), quality=90)
    image.save(root / ('icc.' + suffix), quality=90, icc_profile=profile)
    image.save(root / ('bad-icc.' + suffix), quality=90, icc_profile=b'invalid ICC profile')
    exif = Image.Exif()
    exif[274] = 6
    image.save(root / ('rotated.' + suffix), quality=90, exif=exif)
alpha = image.convert('RGBA')
alpha.putalpha(Image.linear_gradient('L').resize(image.size))
alpha.save(root / 'alpha.webp', lossless=True)
image.save(root / 'animated.webp', save_all=True, append_images=[Image.new('RGB', image.size)], duration=100, loop=0)
