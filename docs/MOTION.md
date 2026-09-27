# Motion

FlowSwitch's motion follows one rule: **responsive first, cinematic second.** Anything the user
waits on becomes readable in about 0.3 s or less. Long, soft tails are reserved for light, color
and ambient movement, which nobody waits for.

All tokens live in `src/FlowSwitch.Core/Animation/MotionProfile.cs`; every moving thing in the
overlay and in the Settings preview reads its timing from there.

## Springs, not timelines

Almost everything is a damped spring, integrated in closed form (`Animation/Spring.cs`), so it is
frame-rate independent — 60 Hz and 240 Hz produce the same curve — and it can be retargeted at any
moment without a visible seam. A spring is described the way motion designers think about it:

- **Response** — the period of the undamped oscillation, roughly "how long the move feels".
- **Damping ratio ζ** — 1 settles without overshoot; slightly below 1 adds a hint of life.

This is what makes interruptions free: a new Tab press, a search result or a window closing
simply moves the target. Nothing is ever queued behind an animation that must finish first.

## Tokens (Smooth preset)

| Token | Spring / duration | Used for |
|---|---|---|
| Micro | 160 ms, ζ 1 | hover, press, search pill |
| Rotor | 300 ms, ζ 0.86 | the orbital rotation following selection |
| Layout | 360 ms, ζ 0.90 | re-layout: search filtering, windows opening/closing, groups |
| Presence | 240 ms, ζ 1 | cards appearing and disappearing |
| Color | 300 ms, ζ 1 | ambient light and glow color following the selected app (~230 ms to 95%) |
| Stage | 400 ms, ζ 0.92 | compact → expanded view |
| Resize | 200 ms, ζ 1 | cards and orbits gliding to a new window size (~150 ms to 95%) |
| Parallax | 600 ms, ζ 1 | pointer parallax |
| Reveal | 200 ms, 22 ms stagger | cards unfolding when the overlay appears |
| Reveal blur | 260 ms | backdrop blur and dimming |
| Commit | 190 ms | the card merging into the real window |
| Cancel | 160 ms | rewind and fade on Esc |
| Breathing | 1.2 %, 4.6 s | the selected card at rest |

Tweens that do exist (reveal, exit, cross-fade) use four cubic Béziers:

| Easing | Control points | Character |
|---|---|---|
| Enter | (0.10, 0.90, 0.20, 1.00) | arrives fast, lands softly |
| Exit | (0.55, 0.00, 0.90, 0.40) | leaves without lingering |
| Standard | (0.20, 0.00, 0.00, 1.00) | movement between two visible states |
| Soft | (0.33, 0.00, 0.20, 1.00) | light and blur |

## The rotor: inertia without lag

Selection is not animated card by card. One continuous value — the rotor
(`Animation/OrbitalRotor.cs`) — chases the integer selection, and the whole layout is a function of
it. That gives the system its physical feel:

- **Unwrapped positions.** Going from the last window to the first keeps turning the same way
  instead of spinning back.
- **Backlog stiffening.** Each extra item of distance beyond one shortens the spring's response
  (factor `1 / (1 + 0.55 · backlog)`, never below 40 %). Tab-Tab-Tab visibly accelerates, then
  settles softly once presses stop.
- **Maximum lag.** The visual never trails the selection by more than 2.6 items; beyond that it
  jumps ahead while keeping its velocity. Fast typists are never waiting for the picture to catch up.
- **Velocity into the layout.** Orbits lean slightly into the direction of travel and relax as the
  rotor slows (`SolarSystemLayout`, "swing").

Rows (Carousel, Cover Flow) don't wrap: going past the end travels back across the row instead of
teleporting.

## Moments

- **Tap.** Alt + Tab released within the reveal delay (70 ms) switches with no overlay at all. A
  340 ms trace of light around the activated window confirms where you landed (can be disabled).
- **Reveal.** Backdrop blur and dimming ease in with *Soft*. Cards rise out of the center of the
  system to their places with *Enter*; the selected card leads and the others follow in order of
  their distance from it along the orbit, 22 ms apart.
- **Rotate.** The previous card shrinks back onto its orbit along an arc while the next one travels
  to the center, grows and gains its info strip; the ambient light cross-fades in OKLab so hues pass
  through natural in-betweens instead of grey.
- **Commit.** The selected card flies to the activated window's real rectangle and scales to it,
  while the rest of the system opens up slightly (+5 %) and fades — the card *becomes* the window.
- **Cancel.** The whole system settles slightly inward (−6 %) and fades, faster than a commit.
- **Idle.** Orbits sway by at most ±2.2° over a 22 s period, cards float by a few pixels, the
  selected card breathes by 1.2 %. It should be felt, not noticed.
- **Hover.** +2.8 % scale, brighter edge and glow, and a tilt of at most 3° toward the pointer.
- **Resize.** Changing the window size (Settings slider or preset) while the switcher or the
  Settings preview is open lays the system out at the new size at once, then every card glides
  from where it was to its new place and size, and the orbit lines morph to their new radii — an
  orbit that is added grows out of the outermost one. Because the transition starts from what is on
  screen, even an added orbit or a re-solved arrangement never makes a card jump; dragging the
  slider simply keeps retargeting it. The next Alt + Tab opens directly at the new size.

## Presets

| | Rotor | Layout | Reveal | Commit | Breathing | Idle |
|---|---|---|---|---|---|---|
| Smooth | 300 ms, ζ 0.86 | 360 ms, ζ 0.90 | 200 ms | 190 ms | 1.2 % | 1× |
| Snappy | 220 ms, ζ 0.95 | 280 ms, ζ 0.95 | 150 ms | 150 ms | 0.8 % | 1× |
| Cinematic | 380 ms, ζ 0.82 | 460 ms, ζ 0.84 | 260 ms | 230 ms | 1.6 % | 1.3× |
| Minimal | 240 ms, ζ 1 | 300 ms, ζ 1 | 140 ms | 130 ms | — | — |
| Custom | from Duration × Stiffness × Damping | | | | | |

The Solar System and Animations pages multiply these by the intensity sliders (scale, rotation,
depth, parallax), and *Idle drift* scales the idle amount.

## Reduced motion

When Windows' "Animation effects" is off, or *Reduced motion* is set to On, FlowSwitch keeps every
state change but removes travel: no rotation, parallax, idle drift or breathing; the rotor jumps,
and the selection **cross-fades in place** over 140 ms. Reveal and exit become short fades, and a new
window size applies at once. The Settings app follows the same system setting for its own transitions.
