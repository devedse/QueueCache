# QueueCache icon

Pixel-art RAM module → queued data blocks → disk. Original artwork generated using the built-in image-generation tool; no third-party brand artwork used. The solid navy background is intentional (not simulated transparency).

- `queuecache.png`: selected generated artwork.
- `queuecache.ico`: Windows icon with 16, 24, 32, 48, 64, 128 and 256px frames, used by the desktop/CLI executables, window chrome, shortcuts and installer.
- Rebuild the ICO on Windows with `pwsh -File build/Convert-AppIcon.ps1`. Nearest-neighbour resampling preserves the pixel-art edges. Image generation is not a CI dependency.

Generation prompt: A cool, nerdy pixel-art Windows application icon for QueueCache, which caches disk writes in RAM and drains queued blocks to disk. A compact RAM module with gold contacts above a recognizable hard-drive platter; three bold teal data blocks and a downward arrow connect them. Crisp chunky square pixels, tight silhouette, few readable shapes, flat front view with slight pixel-art depth. Teal, dark navy, silver and gold. Centered square composition, no text, fine circuitry, glow, blur, watermark or existing logos.

Final edit prompt: Keep the RAM chip, three queued data blocks, arrow and hard drive in the same arrangement and style. Replace all generated gray checkerboard background with an opaque dark navy tile (#101C30); no checkerboard, background texture, decoration or text. Preserve crisp pixel edges and centered framing.
