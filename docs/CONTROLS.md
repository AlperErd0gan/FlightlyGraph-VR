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

## Start screen

When the app opens the graph is hidden and one panel floats in front of you
(`StartPanel`, adds itself to the scene): **Start exploring** shows the graph
with its intro animation, **Guided tour** does the same and then starts the
tour, **Controls** shows the controller controls (Back returns). Above it a
small hologram of the network (all airports, the 300 busiest routes) turns
slowly and now and then morphs onto a small map of Europe and back. Only one panel at a time: opening the dashboard or the timeline
closes the start panel and the other way round. Point and
pull the trigger (pinch with hands) to choose. Later, small **Home** and **Menu** buttons
on the left wrist (shown when the inside of the wrist faces you): Home brings the
panel back, Menu opens / closes the dashboard (also when the headset / Link takes the ≡ button); the graph stays where it is. While the panel is open the assistants' caption ("… is listening",
answers) is hidden. The Gemini voice assistant stays off (not connected,
Y ignored, no orb; the Wit assistant waits too) until Start exploring / Guided
tour and the graph's opening animation after it have ended (`StartPanel.geminiAfterStart`), only once: Home later leaves it on. StartPanel is
added at runtime, so to turn this off add a `StartPanel` component to the scene and untick
it before Play.

## Graph (nothing open)

| Action | Controller | Hands | Editor |
|---|---|---|---|
| Select an airport | point at it, trigger | point, pinch | click |
| See an airport's name | point at it, no trigger (`HoverLabel`) | point at it | hover the mouse |
| Select a route (arc) | point at the arc, short trigger on empty space | short pinch | click the arc |
| Route between two airports | with one selected: hold the trigger on a second airport (it grows; release when shown), or point at it and press A / X | hold pinch on the second airport | Shift + click the second airport |
| Clear the selection | short trigger on empty space (no arc nearby) | short pinch on empty space | Esc, or click empty space |
| Teleport | hold the trigger on the floor, aim the ring, release (and the rig's thumbstick teleport) | hold pinch on the floor, release | — |
| Top routes ↔ regional clusters | L3 (left stick click) | left palm up + pinch | V |
| Open / close the dashboard | left menu button (≡), or **Menu** on the left wrist (turn it to you) | **Menu** on the left wrist | F1 |
| Timeline on / off | dashboard → Timeline | dashboard → Timeline | T |
| City groups on / off | dashboard → Clusters → Group cities | same | C |
| Map of Europe ↔ 3D layout | B | dashboard → Map view / 3D view | M |
| Live flights (flight simulation) | dashboard → Live flights | same | P |
| Go to the graph centre (teleport, face the front) | R3 (right stick click) | dashboard → Controls → Go to graph centre | H |
| Back to the start screen | turn the inside of the left wrist to you, press **Home** on it | same | Home |
| Fly around (desktop only) | — | — | W A S D move, Q / E down / up, Shift fast; arrows or right-drag look; scroll / Z X zoom; Ctrl + scroll or + / − speed; R reset |

Pointing a ray at an airport (or the mouse in the Editor) shows its name right
on top of it (its code hides meanwhile; on the map the name lies on the map like
the country names), without selecting it (`HoverLabel`, adds itself to the scene; untick
`showOnHover` to turn it off).

**Feedback.** Pressing a button or an airport clicks at once; the action itself
runs on release, so moving off a button before releasing cancels it. With
controllers, panel buttons and picked aircraft / routes also vibrate briefly,
and a found connection vibrates more firmly (`ControllerHaptics`); airports
vibrate through the XR rig's own `SimpleHapticFeedback` on hover and select.
Mistakes are said on the caption below your view: A / X with nothing selected,
A / X pointing at nothing, no route between two airports.

The short trigger / long trigger split on empty space: shorter than 0.35 s is a
tap (route pick or clear), longer starts the hand teleport.

The scene's floor is a room (`MovementBounds`, adds itself to the scene):
see-through grid walls (3 m high) stand on the edges of the floor
(`GraphLoader.floor`, else the largest teleport area). Teleports, thumbstick
and walking through a wall put you back inside; a wall shows only
when you come within 1.5 m of it. The graph's centre is kept inside too:
0.5 m from the walls, between 0.3 m above the floor and the walls' top.

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
| route from London to Ankara · connect Paris and Rome · how do I get from X to Y | fewest-stops route; while Live flights is open, a real connection of that day |
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

## Graph search (bar under the info card)

While an airport is selected, a bar hangs under the info card (`GraphSearchView`,
adds itself to the scene). Point and pull the trigger (pinch / click) on its
buttons. The search starts at the selected airport and uses only the routes the
graph shows (timeline month, filters, city groups).

| Button | Does |
|---|---|
| **BFS** | breadth-first: every airport with a direct flight lights up, then those 1 transfer away, then 2, ... (colour per layer) |
| **DFS** | depth-first: follows one branch as deep as it goes, then goes back up (white cursor) |
| **1 transfer** / **2 transfers** | airports reachable with at most one / two changes of plane (direct flights included), with the counts |
| **Pause** / **Slower** / **Faster** / **Stop** | while a search plays; **Again** replays it when it has ended |

Each airport is joined to the one it was found from; the selected airport's
yellow routes are dimmed while a search shows and come back on Stop. The bar hides while a route between two airports is shown. From a big hub
almost everything is direct or 1 transfer away; a small airport shows the
layers better. The bar shows only together with the info card (it hides with it,
e.g. while the dashboard is open).

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
| Flat map ↔ dome | **Dome** / **Flat map** button, low on the left of the view (EUROCONTROL data only) | same | G |
| Closer look | walk / teleport to the map | same | fly camera |

**Dome** (`DomeToggle` + `GeoMapView.SetDome`): you at the centre of the Earth.
The whole globe is around you, `domeRadius` 6 m away, at true scale: every
airport and route is in its real direction from the Earth's centre (New York
58° from the map centre, Singapore 93°). The sharp map of Europe is in front of
you, its centre 40° up (`domeElevation`), north up, east right; the North Pole
is about overhead, the Americas on your left, Asia on your right, Africa below
in front. The rest of the world is a world map in the same dark style
(`globe_dark.png`, `scripts/build_globe.py`, loaded the first time the dome
opens). Airports beyond Europe sit at their true places with their names, routes
follow great circles. At 40° only São Paulo is under the floor (logged).
Everything on it is scaled by domeRadius / the flat map's distance (×3.3) so it
looks as big as on the flat map. You are first put at the graph centre. Leaving
the map (B, dashboard, tour) always returns to the flat map next time.

## Live flights (dashboard → Live flights / P)

Every flight of one day on the graph's routes (the busiest day of August 2025,
Fri 29 Aug: ~25,000), each a small aircraft moving in accelerated time. On the
map they fly at their real positions (EUROCONTROL tracks where present, else
the great circle with the real times) and appear / disappear where they cross
the map's rim; in the 3D layout they fly along the route arcs. Only aircraft in
the air are drawn. While the panel is open the route arcs are hidden (only the
aircraft and their real paths show) and the graph shows that month (sizes);
closing restores both.

Colours give the flight phase, from the recorded flight levels: **green**
climbing, **pale blue** cruising, **lavender** descending (more than 300 ft/min
up or down counts as climbing / descending); the legend under the panel's chart
shows how many are in each phase now (`FlightSimulator.colorByPhase` turns it
off).

**Selecting an aircraft** (TASKS B24): it turns yellow and larger, its whole
path is drawn, both airports light up and a FLIGHT card shows the route,
flight level, ground speed, climb / descent rate, off-block and arrival times
(UTC), how much is flown, operator (ICAO code; "anonymised" where the data
hides it), aircraft type and whether the path is recorded or approximate. The
card follows the aircraft and updates twice a second. Airports still take a
tap first; while the live flights are shown, a tap near the map's busy areas
usually finds an aircraft rather than a route.

**Real connections** (TASKS B7): while the Live flights panel is open, asking
for the route to a second airport (hold the trigger / pinch on it, A / X, Shift
+ click, or by voice) shows a real connection of that day instead of the
network's fewest-stops route: actual flights (passenger flights only, actual
off-block and arrival times), each next one leaving 45 min to 6 h after the
previous one lands at the same airport, at most three flights. The first one
leaving after the panel's time is shown; its flights are drawn in yellow with
their paths and the route animates. The card lists the flights with times
(UTC), the changes and the waiting, the door-to-door time, and a live line
("Now 16:31: flight 1 of 2, in the air at FL297", "changing at BSL, next flight
in 38 min"). With no connection that day the network's route is shown with a
note. Possible, not necessarily bookable (no tickets, alliances, terminals).

| Action (real connection card) | Controller / hands | Editor |
|---|---|---|
| Earlier / Later option of the day | **Earlier** / **Later** on the card | click |
| Play it from ten minutes before it leaves | **Watch** | click |
| Only one operator for all flights (anonymised ones, ~1 in 5, left out) | **Same airline only** checkbox | click |
| Close | X on the card, trigger on empty space, or close the panel | X / Esc |

| Action | Controller / hands | Editor |
|---|---|---|
| Select an aircraft | point at it, trigger / pinch | click it |
| Deselect | X on its card, trigger on empty space, or select an airport / route | X / Esc |
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
