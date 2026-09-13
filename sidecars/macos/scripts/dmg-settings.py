"""Window layout for the sidecar's disk image, read by dmgbuild.

The app on the left, the Applications folder on the right, at a size where the drag between them
is the obvious thing to do. dmgbuild writes the .DS_Store itself, so this produces the same window
on a developer's Mac and on a CI runner with no logged-in session.
"""

import os

application = os.environ["DMG_APP"]
appname = os.path.basename(application)

files = [application]
symlinks = {"Applications": "/Applications"}

# 128pt icons in a 600x400 window: large enough to read the mark, tight enough that both icons sit
# in view without scrolling on a small display.
icon_size = 128
text_size = 13
window_rect = ((200, 160), (600, 400))
icon_locations = {appname: (150, 190), "Applications": (450, 190)}

# The volume takes the app's own icon, so the mounted disk is recognisable in the sidebar.
badge_icon = os.path.join(application, "Contents", "Resources", "AppIcon.icns")

format = "UDZO"
compression_level = 9
