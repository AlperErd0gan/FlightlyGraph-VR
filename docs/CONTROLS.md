# FlightlyVR controls

Every input of the app, by situation. The short version is in the app itself:
dashboard → **Controls** (also shown in the guided tour). Keep both in sync:
the in-app table is `Assets/Scripts/UI/ControlsHelp.cs`.

- **Controller**: Meta Quest Touch controllers. *Select* is the trigger (the
  ray's select action of the XR rig); *A / X* = lower face button of the right /
  left controller; *Y* = upper face button of the left controller (push to talk); *L3* / *R3* = pressing the left / right thumbstick.
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
| Map of Europe ↔ 3D layout | B | dashboard → Map view / 3D view | M |
| Live flights (flight simulation) | dashboard → Live flights | same | P |
| Go to the graph centre (teleport, face the front) | R3 (right stick click) | dashboard → Controls → Go to graph centre | H |
| Fly around (desktop only) | — | — | W A S D move, Q / E down / up, Shift fast; arrows or right-drag look; scroll / Z X zoom; Ctrl + scroll or + / − speed; R reset |

The short trigger / long trigger split on empty space: shorter than 0.35 s is a
tap (route pick or clear), longer starts the hand teleport.

Going to the graph centre teleports you (not the graph) to its centre and
turns you to its front (the biggest hub), in one jump. With
`GraphRecenter.matchEyeHeight` (on by default) your eyes also go to the
centre's height and the teleport floors move along, for seated use or other
heights.

## Voice assistant "Rebecca"

**Hold Y** (left controller; **N** in the Editor), speak, release. Only what is
said while Y is held goes to Wit.ai (Meta Voice SDK; needs internet and an
`AppVoiceExperience` in the scene), and no name is needed. A small caption below
your view shows what was heard and what was done. English.

`VoiceAssistant.mode = AlwaysListening` instead keeps the microphone open and
acts only on sentences starting with **Rebecca** ("Rebecca, show Istanbul";
just "Rebecca" waits 6 s for the command). In that mode everything said near
the microphone is sent to Wit; the name is checked on the returned text.

| Say (holding Y) | Does |
|---|---|
| help | lists the commands |
| show Istanbul · find FRA · where is Munich · (just) London | selects the airport (code, name or city; a city gives its busiest airport) and turns you to it |
| route from London to Ankara · connect Paris and Rome · how do I get from X to Y | fewest-stops route |
| describe · what is this | narrates the selection |
| clear · close | clears the selection |
| regional view · show clusters / top routes · normal view / switch view | view mode |
| map view · show the map · put the airports on the map / 3D view · back to the network view · close the map | map of Europe / 3D layout |
| live flights · show the planes · start the simulation / stop the flights | live flights panel |
| (live flights open) faster · slower · play · pause | its speed / playback |
| filter cargo · only low cost · filter Turkey / reset filters · show everything | market segment or country filter |
| open timeline · play · pause · next / previous month · show April 2020 · show 2021 · all months | timeline |
| open / close dashboard · open insights / filters / controls / overview / find · open airports / routes / clusters page | dashboard |
| group cities / ungroup cities | city groups |
| go to the centre · recenter | same as R3 |
| start tour · (during the tour) next · stop tour | guided tour |
| stop · quiet | stops speech and the timeline |

Editor without speech: type a sentence into `VoiceAssistant.testPhrase` and
press F8 (or the component's context menu → Run Test Phrase).

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
| Sidebar: **Map view** / **3D view** | close the dashboard and move the airports onto the map / back |
| Sidebar: **Live flights** | close the dashboard and open the live flights panel |
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
| Controls: **Go to graph centre** | teleport to the graph's centre, facing its front (biggest hub); `matchEyeHeight` also sets your eyes to its height (seated) |
| Controls: **Buttons** / **Voice** | the button table, or what to say to the voice assistant |

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

## Map view (B / dashboard → Map view / M)

The airports fly from the 3D layout onto a large, gently curved map of Europe
in front of the graph centre; routes arc over the map. Airports beyond Europe
(New York, Dubai, Singapore, ... 22 in all) sit on the dark band outside the
map's blue rim, in their true direction from the map centre, with their code
and city. Everything else works as in the 3D layout: select, info card,
timeline, filters, regional view, city groups. **Go to graph centre** (R3) puts
you in front of the map. Starting the guided tour switches back to 3D.

| Action | Controller | Hands | Editor |
|---|---|---|---|
| Map ↔ 3D layout | B | dashboard → Map view / 3D view | M |
| Closer look | walk / teleport to the map | same | fly camera |

## Live flights (dashboard → Live flights / P)

Every flight of one day on the graph's routes (the busiest day of August 2025,
Fri 29 Aug: ~25,000), each a small aircraft moving in accelerated time. On the
map they fly at their real positions (EUROCONTROL tracks where present, else
the great circle with the real times) and appear / disappear where they cross
the map's rim; in the 3D layout they fly along the route arcs. Only aircraft in
the air are drawn. While the panel is open the routes are dimmed and the graph
shows that month (sizes, routes); closing restores both.

| Action | Controller / hands | Editor |
|---|---|---|
| Play / pause | ▶ on the panel | click, or P |
| 15 minutes back / forward | ◀ / ▶ | click |
| Pick a time of day | drag the slider over the chart | drag / click |
| Speed (15 s/s … 30 min/s) | speed button | click, or [ / ] |
| Close | **X** | click |

## Guided tour (dashboard → Tour)

| Action | Controller / hands | Editor |
|---|---|---|
| Next step (skip the narration) | **Next** on the caption | click |
| End the tour | **Stop** on the caption | click |

While the tour runs, everything else (selection, dashboard, view mode, timeline,
city groups, teleport) is locked so the demo cannot be derailed.

## Buttons left free

None of the face buttons is free any more: A / X connection, Y push to talk
(`VoiceAssistant.pushToTalkBinding`), B map view (`GeoMapView.toggleBinding`).
The unmerged `flight-sim` branch uses B and Y for its playback, so merging it
needs its controls moved (e.g. to its own panel). The view toggle and re-centre
can be moved with `GraphViewMode.toggleBinding` and
`GraphRecenter.recenterBinding`.
