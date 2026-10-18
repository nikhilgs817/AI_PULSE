# AIPULSE branding

The approved wordmark draws AIPULSE as a cyan-to-violet ECG trace, connecting machine health with AI workload monitoring. The compact icon retains its opening heartbeat motif.

- [Dark wordmark](../src/AiPulse/Assets/wordmark-dark.png)
- [Transparent wordmark](../src/AiPulse/Assets/wordmark.png)
- [App icon PNG](../src/AiPulse/Assets/pulse.png)
- [Windows ICO](../src/AiPulse/Assets/pulse.ico)

These raster assets were created with the built-in image-generation tool, approved by the project owner, and adapted into the application and installer. The ICO is a format conversion of the matching icon.

Original direction: “AIPULSE — can we try writing this like an ECG pulse for logo”. The production prompt specifies readable uppercase AIPULSE, a heartbeat peak integrated into the opening A/I, continuous horizontal traces, restrained cyan-to-violet color, and a charcoal dashboard background. Transparent and icon versions preserve that visual identity.

## 0.2.0 desktop icon

The desktop/tray/installer icon now uses a white ECG mark over a smooth electric-blue, indigo, violet and magenta gradient, following the owner's reference. Rounded corners have transparent alpha instead of a white matte. The wordmark is unchanged, with a smaller presentation in compact mode.

Created with the built-in image-generation tool. Final prompt: “Preserve the recognizable single ECG pulse silhouette, crisp solid white centered with generous padding. Replace the dark tile with a smooth vivid multicolor fading background: electric blue upper left, indigo center, violet and magenta lower right. Flat polished rounded-square icon; transparent outside corners; no white matte, texture, shadow, text or extra motifs.”

`scripts/update-icon.ps1` verifies corner transparency and converts the PNG into a seven-frame Windows ICO (16, 24, 32, 48, 64, 128 and 256 pixels). It does not redraw or recolor the generated artwork.
