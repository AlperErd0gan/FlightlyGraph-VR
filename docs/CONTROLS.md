# FlightlyVR controls

Every input of the app, by situation. The short version is in the app itself:
dashboard → **Controls** (also shown in the guided tour). Keep both in sync:
the in-app table is `Assets/Scripts/UI/ControlsHelp.cs`.

- **Controller**: Meta Quest Touch controllers. *Select* is the trigger (the
  ray's select action of the XR rig); *A / X* = lower face button of the right /
  left controller; *L3* / *R3* = pressing the left / right thumbstick.
- **Hands**: hand tracking (no controllers). *Pinch* = thumb and index together.
- **Editor**: Unity Play mode on the PC with the XR Device Simulator / mouse and
  keyboard. With the headset on Link, the controller column applies.

## Graph (nothing open)

| Action | Controller | Hands | Editor |
|---|---|---|---|
| Select an airport | point at it, trigger | point, pinch | click |
| Select a route (arc) | point at the arc, short trigger on empty space | short pinch | click the arc |
| Route between two airports | with one selected: hold the trigger on a second airport (it grows; release when shown), or point at it and press A / X | hold pinch on the second airport | Shift + click the second airport |
| Clear the selection | short trigger on empty space (no arc nearby) | short pinch on empty space | Esc, or click empty space |
| Teleport | hold the trigger on the floor, aim the ring, release (and the rig's thumbstick teleport) | hold pinch on the floor, release | — |
| Top routes ↔ regional clusters | L3 (left stick click) | left palm up + pinch | V |
| Open / close the dashboard | left menu button (≡) | — | F1 |
| Timeline on / off | dashboard → Timeline | dashboard → Timeline | T |
| City groups on / off | dashboard → Clusters → Group cities | same | C |
| Re-centre the graph (seated, other height) | R3 (right stick click) | dashboard → Controls → Re-centre graph | H |
| Fly around (desktop only) | — | — | W A S D move, Q / E down / up, Shift fast; arrows or right-drag look; scroll / Z X zoom; Ctrl + scroll or + / − speed; R reset |

The short trigger / long trigger split on empty space: shorter than 0.35 s is a
tap (route pick or clear), longer starts the hand teleport.

Re-centring moves the graph's centre to your eyes and turns its front (the
biggest hub) to where you look, in one jump. It goes no lower than keeps every
airport above the floor, so very low seats get the graph slightly above eye
height.

## Info card (an airport, route or connection selected)

Appears below and to the right of your gaze, with a line to the selection.

| Action | Controller / hands | Editor |
|---|---|---|
| Close it (clears the selection) | point at its **X**, trigger / pinch | click X, or Esc |
| Another airport | select it as usual | click |
| Route from the selected airport | hold select on the other airport, or A / X | Shift + click |

It hides while the dashboard is open and closes by itself if you walk or
teleport more than 5 m away from where you selected.

## Dashboard (left menu button / F1)

All buttons: point + trigger (or pinch / poke with hands), click in the Editor.

| Where | What it does |
|---|---|
| Sidebar: Overview … Controls | switch page |
| Sidebar: **Timeline** | close the dashboard and open the timeline |
| Header: **Tour** | start the guided tour |
| Header: **X** | close the dashboard |
| Overview: **Play over time** | open the timeline and play the months |
| Insights: a row | airport: select it and turn towards it; route: close the dashboard and show it |
| Airports: a row | select the airport and turn towards it |
| Routes: a row | close the dashboard and show the route |
| Clusters: **Show regional view / top routes** | switch the view mode |
| Clusters: **Group cities / Show single airports** | city groups on / off |
| Selected airport: **Pin to compare** / **Unpin** | compare two airports side by side |
| Find airport: By name / country / traffic, letters, Prev / Next | sort, jump, page; a row selects the airport and turns you to it |
| Filters: segment, country, minimum flights, **Reset filters** | show part of the network |
| Controls: **Re-centre graph** | bring the graph to your eye height, facing you |

## Timeline (time slider)

| Action | Controller / hands | Editor |
|---|---|---|
| Play / pause | ▶ button | click, or T to open / close |
| One month back / forward | ◀ / ▶ buttons | click |
| Pick a month | drag the slider over the chart (or point at a month and select) | drag / click |
| Speed (1 / 2 / 4 months per second) | speed button | click |
| All months together | **All months** | click |
| Close (back to all months) | **X** | click, or T |

While a month is shown, airport sizes, the visible routes, the info card and the
dashboard lists follow it; the selection stays.

## City groups (Clusters page / C)

| Action | Controller / hands | Editor |
|---|---|---|
| Open a city (airports fly out) | select the city node (sphere with a ring) | click it |
| Close it again | select its label ("London · group") | click the label |
| Show a hidden airport | select it anywhere (dashboard lists, routes): its city opens | — |

## Guided tour (dashboard → Tour)

| Action | Controller / hands | Editor |
|---|---|---|
| Next step (skip the narration) | **Next** on the caption | click |
| End the tour | **Stop** on the caption | click |

While the tour runs, everything else (selection, dashboard, view mode, timeline,
city groups, teleport) is locked so the demo cannot be derailed.

## Buttons left free

B and Y are not used by the app (they usually mean back / cancel; the unmerged
`flight-sim` branch uses them for its playback). The view toggle and re-centre
can be moved to other buttons with `GraphViewMode.toggleBinding` and
`GraphRecenter.recenterBinding`.
