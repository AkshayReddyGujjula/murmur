# Overlay motion refinement

The recording silhouette remains 132 logical pixels wide. Height changes from 36
to 37.8 in the first pass, then another 10% to 41.58 logical pixels before device-pixel rounding. A transparent 2-pixel inset
allows restrained spring motion without shifting the visible 24-pixel screen margin.
The native surface is reused during the normal recording/loading/result sequence.

Changes:

- 27 pixel-aligned rounded bars with clear gaps replace the second pass's 34 bars
  (another 20.6% fewer), with strokes 28% thicker than the first 48-bar pass in the same span. Actual
  audio levels drive their energy; a fine contour differentiates adjacent RMS samples.
- A visual-only quiet-level threshold and gentler gain curve reduce sensitivity.
  Microphone capture and transcription are unchanged.
- The follow-up restores 30% response above that calmer pass. The taller pill also
  gives the waveform more vertical range. The latest pass adds another 35% visual
  response (clamped at full scale) and lets peaks reach two logical pixels from
  the top/bottom, retaining the pill's dimensions and using 27 bars.
- The animation timer requests 15 ms frames; elapsed time, rather than tick count,
  controls the spring, crossfades and exit. Actual frame rate depends on Windows.
- Microphone samples are held between callbacks rather than replaced with zeros
  on intervening frames. A damped interpolator provides smooth response and release.
- A continuous three-dot wave replaces the wrapping five-dot loading shimmer.
- Copied results collapse into a black circle with a drawn green tick. Failures
  use a red X and retain the longer error display time. The tray menu retains the
  explanation, including when system notification balloons are disabled.
- The success circle means copied. A skipped auto-paste still leaves the text copied
  and preserves the manual-paste instruction in the tray menu and notification.
- Launch shortcut hints and actionable setup errors retain text. The overlay stays
  click-through and non-activating. No buttons or input focus were added.
- The Windows client-area animation preference disables geometry animation and
  decorative loading-dot motion. Functional microphone levels continue to update.

Validation:

- Full offline suite passed: 415 engine assertions, 372 UI checks, 61 startup checks,
  plus Core, Audio, Wiring and harness groups. One existing handoff reparse test skipped.
- UI and Wiring groups rerun after final refinements: pass, with 375 UI checks.
  Transition-opacity regressions ensure an already-visible pill never flashes
  transparent when changing state.
- Build and diff whitespace checks: pass.
- Native renderer captured 250 frames using simulated audio, with no visible window,
  microphone or provider. Recording, loading and result PNGs inspected directly.
- The spring and waveform tests cover bounded overshoot, preserved dimensions,
  bar spacing, circular completion, reduced motion and stability after delayed frames.
- Independent read-only review informed the physical-pixel alignment, shared drawing
  objects, timer interval and persistent error feedback. A proposal to restore error
  text inside the circle was rejected because it conflicts with the requested design.

Live microphone responsiveness and actual on-screen frame pacing still require user
acceptance. The desktop inspection helper was unavailable in the preceding session;
renderer capture does not establish live window appearance or timing.
