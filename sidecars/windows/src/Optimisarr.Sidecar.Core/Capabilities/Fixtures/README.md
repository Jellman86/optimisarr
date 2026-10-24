# Native capability fixture

`profile.png` is a synthetic 32×32 RGB gradient with a compressed sRGB ICC profile
and a generated EXIF Artist value. It contains no personal media. Its public sRGB
profile comes from the existing synthetic `tools/image-quality-native/fixtures/icc.webp`.
The probe checks complete profile bytes and portable marker read/write after copying
metadata into a stripped PNG and each encoded format.

Regenerate from the repository root with:

```sh
python3 scripts/generate_worker_capability_fixture.py
```

The fixture and ICC hashes are pinned by the prober. Changed fixture bytes require
updating those hashes and rerunning native qualification on supported hosts.
