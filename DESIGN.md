# Patchboard design

Dark, monochrome, dense. It is a tool you glance at mid-game, not something you read.
Resanance's UI is grey boxes with hairline borders and centred text that truncates.
Everything here is a reaction to that.

## Palette

Near-black and off-white, matching Samuel's standing preference for monochrome UI.
One accent only, and it is functional rather than decorative: it marks what is
currently making noise, which is the single thing you need to find in a hurry.

    --bg            #0a0a0a   window background
    --surface       #141414   button face, panel background
    --surface-hover #1e1e1e   hover
    --surface-down  #2a2a2a   pressed
    --border        #2a2a2a   hairlines, 1px
    --text          #f5f5f5   primary
    --text-dim      #8a8a8a   secondary, device names, hints
    --accent        #4ade80   PLAYING only. Never used for decoration.
    --danger        #f87171   stop-all, destructive confirms, device gone

## Type

Segoe UI Variable, the Windows 11 default, falling back to Segoe UI.
Button labels 13px semibold. Panel labels 12px regular. Hints 11px in --text-dim.
Never centre a long label and let it clip. Labels wrap to two lines then ellipsis,
with the full name in a tooltip. This is the specific Resanance failure visible in
Samuel's screenshot, where "Minecraft Nostalgi" and a four-line title overflow their cells.

## Buttons

Rounded 6px. No gradients. 1px --border. The whole cell is the hit target.
A playing button shows a 2px --accent left edge and a thin progress line along the
bottom, so you can see both that it fired and how much is left.
An image, when set, fills the cell and the label sits on a bottom gradient scrim so
text stays readable over any artwork.
Hotkey hint sits top-right in --text-dim, 10px.

## Layout

Grid of buttons fills the window and reflows on resize. Rows and columns are set in
settings, and the grid scrolls when there are more sounds than cells.
Devices live in a left panel that collapses, not a modal, because you change routing
while listening. Output devices on top, input below, each a checkbox with a volume
slider and a live level meter.

## Motion

Fast and small. 120ms hover, 80ms press. Nothing eases longer than 200ms.
No animation on the grid itself, since it can hold a hundred buttons.

## What this is not

No gradients, no glass, no drop shadows beyond a single subtle panel edge, no
decorative colour. If a colour appears, it is telling you something is playing or
something is wrong.
