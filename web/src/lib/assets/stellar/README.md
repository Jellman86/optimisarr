# Stellar cube textures

The three JPEGs are original AI-generated star fields created for Optimisarr's
Stellar cube design. Blue stars, pink nebulae and gold dust are mapped to separate
faces. They are bundled with content hashes and loaded only when an animated
Stellar icon is visible; reduced-motion sessions use the small pre-rendered stills.

The renderer lives in `stellar-renderer.ts` and the continuous idle/work/settling
motion in `stellar-motion.ts`. Run `npm run brand:assets` from `web` after changing
those files or these textures to regenerate the committed optional Stellar stills
and favicons. The asset generator uses the same renderer with a native
Canvas adapter; it does not run during a production build.

The default Precession renderer, motion, shaders and `/brand` assets remain
separate. Stellar assets live under `/brand/stellar`; Settings → System →
Appearance saves the selected style in `optimisarr.brand` in local storage.
