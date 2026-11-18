# Research questions: immersive (VR) vs non-immersive (desktop) FlightlyVR

Candidate research questions for a study that compares the same FlightlyVR app in two
conditions:

| | **Immersive (IMM)** | **Non-immersive (DESK)** |
|---|---|---|
| Display | Meta Quest HMD (Link or standalone APK), stereo, head-tracked, 72 Hz | Desktop monitor, mono |
| Navigation | Walk / turn the head, teleport (trigger on floor), R3 recenter | Fly camera: W A S D, Q / E, right-drag look, scroll zoom (`FlyCamera`) |
| Selection | Ray + trigger (or pinch), hover names (`HoverLabel`) | Mouse click, mouse hover |
| Route between two airports | Hold trigger on the second airport / A or X | Shift + click |
| Menus | World-space panels (dashboard, timeline, live flights) pressed with the ray | Same panels, clicked with the mouse |
| Voice | Hold Y, speak (Wit / Gemini) | Hold N, speak (same commands) |

The data, layouts (3D, map, dome), panels and voice commands are the **same** in both
conditions. Only display + input change, which is what makes the comparison clean.

References use the keys in `docs/literature_review/refs.bib`.

---

## 0. Framing

Immersive analytics claims that stereo, head tracking and embodied navigation help people
understand complex data (`marriott2018immersive`, `ens2021grand`, `fonnet2021survey`).
For **networks** the evidence is mixed and task-dependent (`kwon2016study`,
`kraus2022survey`, `joos2025survey`, `huang2023embodied`). FlightlyVR adds three things
most graph studies lack:

1. a **geographic** layout (map / dome) next to an abstract 3D layout,
2. a **temporal** axis (68 months, timeline + live flights of one day),
3. **voice** as a second input channel (regex commands + an LLM assistant).

That allows research questions that ask not only *"is VR better?"* but *"for which
network tasks, which layouts and which input channels does immersion matter?"*

---

## 1. Core research questions (recommended for the paper)

### RQ1: Task performance on network tasks
**Does immersion change accuracy and completion time for airport-network tasks, and does
the effect depend on the task type?**

- Tasks, following the graph task taxonomy (`lee2006task`):
  - *Topology:* "Is there a direct route between X and Y?", "fewest stops from X to Y",
    "which airport has the most connections in region R?"
  - *Attribute:* "Which of these 3 airports had more flights in March 2022?"
  - *Overview:* "How many regional clusters are there? Which one contains Istanbul?"
  - *Browsing:* "Name 3 destinations of the busiest link from FRA."
- **H1a:** IMM is more accurate on overview / cluster tasks: depth and wide field of view
  help separate dense regions.
- **H1b:** DESK is faster on precise single-item tasks (attribute lookup), because mouse
  pointing is more precise than ray pointing.
- Measures: accuracy (correct / total), completion time, number of selections per task.
- App support: `GraphSelector` events, `ConnectionFinder`, B21 `StudyLogger` (task times,
  selections).

### RQ2: Spatial and geographic understanding
**Does immersion improve understanding of geographic flow patterns (map / dome) more than
of abstract topology (3D layout)?**

- Conditions × layout: {IMM, DESK} × {3D force layout, flat map, dome}.
- Tasks: "Which country has the most routes to Morocco?", "Is traffic from the UK to
  Spain mostly direct or via hubs?", "Point to the airport furthest from Paris."
- **H2:** The IMM advantage is larger on the map than in the 3D layout, because geography
  gives a stable spatial frame that head movement can exploit (cf. `yang2019flowmaps`).
- **H2b:** The egocentric **dome** does not beat the flat map for comparison tasks
  (`yang2018maps` found the egocentric globe least effective), but may score higher on
  "sense of scale" and engagement.
- Measures: accuracy, time, pointing error (degrees) for "point to" tasks, layout
  preference.

### RQ3: Multimodal (voice) interaction
**Do people rely on voice more in VR than on the desktop, and does voice close the
efficiency gap between the conditions?**

- Same voice commands in both conditions (`VoiceAssistant.RunCommand`): "show Istanbul",
  "route from London to Ankara", "show April 2020", "live flights".
- **H3a:** Voice share (voice commands / all commands) is higher in IMM, because the
  keyboard is unavailable and ray-pointing small targets is costly (`badam2017affordances`,
  `song2025embodied`).
- **H3b:** With voice available, the time difference between IMM and DESK on
  find-an-airport tasks shrinks.
- **H3c:** In IMM, people combine voice with pointing ("route from *this* to Rome")
  more often: deictic speech (`bolt1980put`, `srinivasan2018orko`, `lee2026voice`).
- Measures: command counts per channel, voice error / recognition rate, time per task,
  type of utterance (command, question, deictic).

---

## 2. Secondary research questions

### RQ4: Navigation and embodiment
**Does physical navigation (walking, head turns, teleport) lead to different exploration
strategies than mouse / keyboard navigation?**

- **H4:** IMM users view the graph from more viewpoints (larger range of head positions)
  and use overview → detail transitions more (`huang2023embodied`, `sorger2019dynamic`).
- Measures: path length, number of distinct viewpoints, teleports / fly-camera moves,
  time spent zoomed in vs out (head pose logged by B21).

### RQ5: Mental map and memory
**After switching layout (3D ↔ map) or month (timeline), do people keep their mental map
better in VR?**

- Task: after exploring, close the app and ask participants to place 5 hubs on a blank
  map / sketch; or, after a layout switch, find a previously seen airport again.
- **H5:** IMM gives better recall of airport positions in the 3D layout
  (`kotlarek2020mental`). There is no difference on the map, where geography is the anchor.
- Measures: placement error, re-finding time.

### RQ6: Temporal patterns
**Does immersion help to see change over time (timeline: 2020–2025, COVID drop and
recovery; live flights: the daily wave)?**

- Tasks: "In which month did traffic to Spain recover to 2019 level?", "When does the
  morning peak of departures from London start?"
- **H6:** No accuracy difference for reading values (the panels show the numbers), but IMM
  users notice spatial-temporal patterns (e.g. the east-to-west morning wave of live
  flights) more often in free exploration (`filipov2022time`, `hurter2019fiberclay`).
- Measures: accuracy, unprompted insights about time.

### RQ7: Insight generation in free exploration
**In 10 minutes of free exploration, do people find more, deeper or different insights
in VR than on the desktop?**

- Protocol: think-aloud, then insights coded by type (fact, comparison, trend, anomaly,
  hypothesis) and depth.
- **H7:** Similar number of insights, but more *structural* insights (clusters, hubs,
  bridges) in IMM and more *value* insights (numbers, rankings) on DESK.
- Measures: insight count, category distribution, rated depth (2 coders, Cohen's κ).

### RQ8: User experience, workload, comfort
**How do workload, usability, presence, engagement and simulator sickness differ, and
does the Reduce-motion setting matter?**

- Questionnaires: SUS (`brooke1996sus`), NASA-TLX (`hart1988nasatlx`), SSQ
  (`kennedy1993ssq`, IMM only or both), a short presence / engagement scale, preference.
- **H8a:** IMM is rated more engaging, with equal or slightly higher workload.
- **H8b:** SSQ scores stay low. Teleport rather than smooth locomotion
  (`bozgeyikli2016teleport`) and `Comfort.ReduceMotion` reduce sickness further.
- Exploratory: does Reduce motion lower SSQ without hurting task performance?

### RQ9: Scale and density
**Does the effect of immersion change with network size and visual density?**

- Vary: 100 vs 200 airports, top routes vs all routes, live flights off vs on (up to
  ~25 k aircraft).
- **H9:** The IMM advantage on overview tasks grows with density, until occlusion and
  frame-rate drops cancel it. Check FPS with `PerfMonitor` (X / F3).
- Measures: accuracy × density, FPS per condition.

---

## 3. Exploratory / follow-up questions

- **RQ10: Order and transfer.** Does exploring in VR first help later desktop work
  (or the other way)? Analyse condition order as a factor.
- **RQ11: Guided tour vs free start.** Does the guided tour (`GuidedTour`) reduce
  first-use time more in VR, where controls are less familiar?
- **RQ12: Audio narration.** Does spoken narration of a selected airport (`NodeNarrator`)
  help more in VR, where reading text panels is harder?
- **RQ13: LLM assistant.** With Gemini (realtime voice), do people ask more open
  questions ("why is Palma so busy?") in VR than on the desktop? *Note: Gemini stays off
  on the EUROCONTROL data unless `allowLicensedData`, so this needs the OpenFlights
  dataset or a licence check.*
- **RQ14: Hybrid.** Would people choose to switch between desktop and VR during one
  analysis session, and for which tasks?

---

## 4. Study design notes

- **Design:** within-subjects, condition order counterbalanced (IMM→DESK / DESK→IMM).
  Use two task sets of equal difficulty with different airports, also counterbalanced, so
  answers are not remembered.
- **Sample size:** for a paired comparison with medium effect (d = 0.5), α = .05,
  power = .80 → about **34 participants**. With d = 0.6 → about 24. Run a pilot (5–6
  people) to estimate the real effect sizes.
- **Session:** ~60–75 min: consent, training in both conditions (same training tasks),
  tasks, questionnaires after each condition, free exploration, interview.
- **Logging:** B21 `StudyLogger` (task times, selections, voice / controller commands,
  view changes, timeline use, head pose); participant codes only (P01, …), no names.
- **Analysis:** mixed-effects models (participant random effect; condition, task type,
  layout fixed effects). Time log-transformed, accuracy as logistic. Questionnaires with
  Wilcoxon signed-rank tests.

### Fairness of the desktop condition
For the comparison to be fair, the desktop version must be a **real application**, not
"the Unity Editor with a fly camera":

- Build a **Windows desktop build** without XR (or with XR turned off at start) that uses
  `FlyCamera`, mouse selection and Shift+click.
- **Same screen time per frame budget:** IMM over Link runs on the same PC; standalone
  Quest has a weaker GPU. Report FPS for both (`PerfMonitor`).
- **Same text size in degrees of visual angle** where possible: VR panels are read at
  ~1 m, the desktop at ~60 cm.
- **Same voice push-to-talk** (Y vs N) and the same recognition back-end.
- Things that cannot be matched (stereo, field of view, embodiment) are the independent
  variable itself and should be named as such.

### Threats to validity
- **Novelty effect** of VR. Measure prior VR experience and include training time.
- **Ray vs mouse precision** confounds "immersion" with "pointing device". Optional third
  condition: VR with a mouse-like precise pointer, or desktop with a 3D mouse.
- **Learning** across conditions, handled by counterbalancing and separate task sets.
- **Licensed data:** if the EUROCONTROL graph is used in the study, check what the
  licence allows for screenshots in the paper and for recording sessions.

---

## 5. Mapping to the app (what exists, what is missing)

| Needed for | Exists | Missing |
|---|---|---|
| Task timing / logs | `GraphSelector` events, Console logs | **B21 StudyLogger** (not started) |
| Desktop condition | `FlyCamera`, mouse selection, Shift+click, keyboard shortcuts | A clean **desktop build** profile (no XR, start screen usable with the mouse) |
| Layout factor | 3D, map, dome (`GeoMapView`, `DomeToggle`) | A study mode that locks the layout per task |
| Time factor | Timeline, live flights | — |
| Voice channel | `VoiceAssistant`, `GeminiLiveAssistant` | Logging utterance text + recognition success per command |
| Comfort | `Comfort.ReduceMotion`, teleport | — |
| Performance | `PerfMonitor` (X / F3, CSV) | Getting the CSV off a standalone Quest needs adb |
| Task presentation | — | A task panel (question, answer buttons, "done") driven by a task list file |

---

## 6. Suggested scope per venue

- **Short paper / poster (e.g. IEEE VR poster, 2 pages):** RQ1 + RQ8 only, 12–16
  participants, 3D layout only.
- **Full paper (DIS 2027 / HCII 2027, as noted for B21):** RQ1–RQ3 core, RQ4 + RQ8
  secondary, ~24–34 participants.
- **Follow-up:** RQ2 (map vs dome) and RQ3 (voice) can each be their own study.
