# Synthetic preprocessing fixtures

`outlined-subtitle.txt` is a hand-authored synthetic 32×12 monochrome subtitle-like glyph fixture created for this repository. `#` is a white foreground pixel and `.` is a dark background pixel. It is released under CC0-1.0 and contains no game asset, screenshot, font file, or third-party copyrighted image.

The test project copies this file to `fixtures/preprocessing` beside its assembly. Tests resolve that deployed copy directly, so external `GAMELINGOAI_ARTIFACTS_PATH` output directories work without relying on a repository ancestor or the working directory. The source fixture and existing golden output hash are unchanged.
