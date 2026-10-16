# Timber SLS checks: deflection and floor vibration (EN 1995-1-1)

Specification for the *Timber SLS Check* add-on. Every formula below maps to one function in `Core/TimberSlsMath.cs`; every number marked *default* is a starting value in a dropdown that the user can change or replace with *Custom*.

Sources:
- NEN-EN 1995-1-1+C1+A1:2011 (EC5:2004), in this folder: 2.2.3, 2.3.2.2, Table 3.2, 7.2, 7.3.
- Harff et al. (2018), *Houten platte daken en de Eurocode*, as summarised in `Timber_deflectionEC5__SLS.pdf` (house method).
- NEN-EN 1990/NB, A1.4.3 (Dutch limits, as quoted in that PDF).
- EN 1995-1-1:2025, §9.3 (2nd generation, vibration only), from the clause text supplied by the user.

Deflection uses EC5:2004 only. Vibration offers EC5:2004 §7.3 or EN 1995-1-1:2025 §9.3; the user picks one.

---

## 1. Scope and legal status

- Members only (joists, beams, rafters, purlins). Slabs and CLT panels are not in v1.
- Two groups, **Floor** and **Roof**, each with its own scheme, limits and factors, checked in one run. Vibration is checked for floors only.
- In NL, deflection limits are not legally required: the Bbl (art. 4.12–4.14) covers collapse (ULS) only. Limits apply by agreement with the client. The defaults follow the NL NA; the report states which limits were used.
- Outside NL: the same checks work, with the NA presets switched or every value set to *Custom*.

## 2. Definitions

The two notations describe the same deflections; the add-on shows both.

| EC5 (fig. 7.1) | NEN-EN 1990/NB (fig. A1.1) | Meaning |
|---|---|---|
| w_inst | w1 + w3 (elastic part) | Instantaneous deflection, E_mean |
| w_creep | w2 | Creep deflection |
| w_fin = w_inst + w_creep | w_tot = w1 + w2 + w3 | Final deflection |
| w_c | w_c | Precamber (*zeeg*) |
| w_net,fin = w_fin − w_c | w_max = w_tot − w_c | Net final sag below the chord |
| (no term) | w_bij = w2 + w3 | Additional deflection after the finishes are in (*bijkomende doorbuiging*) |
| l | l_rep | Span; for a cantilever l_rep = 2 × length (factor editable) |

NL NA split (Harff reading):
- w1: elastic deflection under G + ψ2·Q
- w2: creep of that same load, kdef·(G + ψ2·Q)
- w3: the remaining variable load, (1 − ψ2)·Q, elastic

## 3. Stiffness, creep and combination factors

**Stiffness.** SLS deflections use E_mean, G_mean, K_ser (2.2.3(2)). Vibration uses E_mean. The AxisVM linear analysis is run with the material's E_mean; the add-on applies creep afterwards, per load case (2.2.3(5)). Members with mixed creep behaviour (2.2.3(4), E_mean,fin = E_mean/(1+kdef)) are out of scope for v1 and give a warning.

**kdef** (Table 3.2), default for each material × service class:

| Material | Standard | SC1 | SC2 | SC3 |
|---|---|---|---|---|
| Solid timber | EN 14081-1 | 0.60 | 0.80 | 2.00 |
| Glulam | EN 14080 | 0.60 | 0.80 | 2.00 |
| LVL | EN 14374, EN 14279 | 0.60 | 0.80 | 2.00 |
| Plywood EN 636-1 / -2 / -3 | EN 636 | 0.80 | –/1.00/1.00 | –/–/2.50 |
| OSB/2 | EN 300 | 2.25 | – | – |
| OSB/3, OSB/4 | EN 300 | 1.50 | 2.25 | – |
| Particleboard P4 / P5 | EN 312 | 2.25 | –/3.00 | – |
| Particleboard P6 / P7 | EN 312 | 1.50 | –/2.25 | – |
| Hardboard HB.LA / HB.HLA1,2 | EN 622-2 | 2.25 | –/3.00 | – |
| Medium board MBH.LA / MBH.HLS | EN 622-3 | 3.00 | –/4.00 | – |
| MDF.LA / MDF.HLS | EN 622-5 | 2.25 | –/3.00 | – |

- The material is read from the AxisVM material name where possible (C24, GL24h, …); otherwise the user picks it. *Custom* takes any kdef.
- Connections with the same creep behaviour: 2·kdef (2.3.2.2(3)). Two materials: kdef = 2·√(kdef,1·kdef,2) (2.13). These are listed for completeness; the member check uses the member material's kdef.

**ψ factors.** Taken from the AxisVM load group of each load case (see §6). The defaults below fill the table when a model has no groups.

| Category | ψ0 | ψ1 | ψ2 |
|---|---|---|---|
| A domestic | 0.4 (NL) / 0.7 (EN) | 0.5 | 0.3 |
| B office | 0.5 (NL) / 0.7 (EN) | 0.5 | 0.3 |
| C congregation | 0.6 (NL) / 0.7 (EN) | 0.7 | 0.6 |
| D shopping | 0.4 (NL) / 0.7 (EN) | 0.7 | 0.6 |
| E storage | 1.0 | 0.9 | 0.8 |
| H roofs | 0 | 0 | 0 |
| Snow (≤ 1000 m) | 0 (NL) / 0.5 (EN) | 0.2 | 0 |
| Wind | 0 (NL) / 0.6 (EN) | 0.2 | 0 |

The NL column is from memory of NEN-EN 1990/NB Table A1.1. **VERIFY against your NB copy before release.** Whatever the defaults, the model's own load-group values win.

## 4. Deflection schemes

Inputs per load case i: the instantaneous relative deflection w_inst,i(x) from AxisVM (see §5), its role (G or Q), ψ0,i, ψ1,i, ψ2,i, and kdef of the member. Below, w_G = Σ over the permanent cases and w_Q,j = the variable case (or group) j. With several variable groups, each one is taken as leading in turn and the envelope is used.

### A. EC5 2.2.3(5) (FinWood / AxisVM style)

- w_inst = w_G + w_Q,1 + Σ ψ0,i·w_Q,i (characteristic, 2.2.3(2))
- w_fin = w_G·(1 + kdef) + w_Q,1·(1 + ψ2,1·kdef) + Σ w_Q,i·(ψ0,i + ψ2,i·kdef)   (2.2–2.5)
- w_net,fin = w_fin − w_c   (7.2)
- Checks: w_inst ≤ l/x_inst, w_net,fin ≤ l/x_net, w_fin ≤ l/x_fin (Table 7.2 ranges, §5.2)

### B. Harff / NL (default)

- w_eind = w_G·(1 + kdef) + w_Q,1·(1 + ψ2,1·kdef) + Σ w_Q,i·(ψ0,i + ψ2,i·kdef) − w_c ≤ l_rep / 250
- w_bij = w2 + w3 = w_G·kdef + w_Q,1·(1 + ψ2,1·kdef − ψ2,1) + Σ w_Q,i·(ψ0,i + ψ2,i·kdef − ψ2,i) ≤ 3/1000·l_rep (or l_rep/500 with brittle partitions)
- For roofs (ψ2 = 0): w_bij = kdef·w_G + w_Q.
- **Deviation from the NA** (as in the PDF): the NA prescribes the frequent combination for w2 + w3. Harff uses quasi-permanent, because only G + ψ2·Q creeps. Since ψ1 ≤ 1, Harff's w3 = (1 − ψ2)·Q is at least as strict as the frequent (ψ1 − ψ2)·Q.
- **Optional variant "NA frequent, w1 = G"**: w_bij = kdef·(w_G + ψ2·w_Q) + ψ1·w_Q. This exceeds Harff when 1 − ψ2 < ψ1 (categories C, D, E). A checkbox runs both and takes the larger.

### C. Conservative (AxisVM "conservative method")

- w_fin = (w_G + w_Q,1 + Σ ψ0,i·w_Q,i)·(1 + kdef): the characteristic combination with all loads creeping as if permanent. It overestimates; use it as a quick check.

### D. Custom

- w = Σ c_role·w_role, with the user setting c for each role (G, Q-leading, Q-accompanying), separately for each check row.

### Worked check (Bastion IX, Brielle)

- kdef = 2.00, ψ2 = 0.40, w_G = 0.04 mm, w_Q = 2.24 mm:
  - w_eind = 0.04·3.0 + 2.24·1.8 = **4.15 mm**
  - w_bij = 0.04·2.0 + 2.24·1.4 = **3.22 mm**
- (In the original report table these two columns are swapped; the formulas are correct.)

## 5. Limits and measurement

### 5.1 Measurement rule

- Deflection is measured **per member, relative to the chord between its own end points**: w_rel(x) = w(x) − [w(0) + (w(L) − w(0))·x/L]. So the deflection of a secondary beam does not include the sag of the primary beam carrying it. This is the NEN 6702 principle, still good practice.
- Direction: local z by default; local y as an option, for purlins on slopes.
- **Cantilever** (flag per member, or auto when one end has no support and no connection): w = tip displacement relative to the root tangent; l_rep = factor × L, with factor default 2.0 (left/right, as in the AxisVM dialog).
- The governing value is max |w_rel(x)| over the section points.

### 5.2 Limit rows

Each group has up to 5 rows, like the AxisVM timber SLS dialog: **criterion** (dropdown) | **relative L/x** | **absolute mm**. When both relative and absolute are filled in, both are checked. Utilisation UC = w / limit, and the governing row is the one with the highest UC.

Presets:

| Preset | Check | Limit | Comb. | Group |
|---|---|---|---|---|
| NL NA, floor | w_eind (w_net,fin) | l_rep/250 | quasi-perm. + creep | Floor |
| NL NA, floor | w_bij | 3/1000·l_rep (≈ l/333) | Harff | Floor |
| NL NA, floor with brittle partitions | w_bij | l_rep/500 | Harff | Floor |
| NL NA, roof (people use it intensively) | w_bij | 3/1000·l_rep | Harff | Roof |
| NL NA, other roof | w_bij | l_rep/250 | char. | Roof |
| NL NA, appearance | w_max | l_rep/250 | quasi-perm. | both |
| NL NA, beams supporting walls | w_bij | 15 mm span / 10 mm cantilever | char. | both |
| EC5 Table 7.2, span | w_inst / w_net,fin / w_fin | 300–500 / 250–350 / 150–300 | | both |
| EC5 Table 7.2, cantilever | w_inst / w_net,fin / w_fin | 150–250 / 125–175 / 75–150 | | both |
| Custom | any | any | any | |

- For Table 7.2 the default is the less strict end of each range; the dropdown also offers the strict end.
- Precamber w_c reduces w_net,fin / w_eind / w_max only; it never helps w_bij.

## 6. Load cases and combinations

The add-on never writes combinations to the model. It reads results per load case and sums them itself, because creep must be applied per case (G and Q creep differently) and AxisVM combination results have already mixed G and Q.

Three sources, picked in the Loads tab:
1. **Auto (default).** Load cases plus their AxisVM load groups: the type (permanent / variable / accidental), ψ0/ψ1/ψ2 and exclusive groups. All SLS combinations are generated internally the way AxisVM's auto-combination does: every variable group leading in turn, the others with ψ0, exclusive groups respected. Every combination is checked, and the governing one is reported per member.
2. **From AxisVM combinations.** The user ticks existing SLS combinations. The factor per load case comes from the combination; the role and ψ2 come from the case's load group, so creep is still applied per case.
3. **Manual.** The role and ψ set per load case, for models without load groups.

Accidental and seismic groups are ignored. Load cases with no results stop the run with "run linear analysis first".

## 7. Floor vibration, EC5:2004 §7.3

Applies to residential floors (7.3.3); offered for every Floor group member, with a warning outside that scope.

**Inputs** per floor field:
- span L: default = member length (largest span for continuous members, conservative single span)
- joist spacing s
- floor width B
- (EI)_L = E_mean·I_joist / s (from AxisVM), in Nm²/m
- (EI)_b, crosswise stiffness of the deck, in Nm²/m (user input)
- mass m in kg/m², = (self-weight + permanent loads)·1000/g, with the unloaded floor per 7.3.3(3) (only G)
- damping ζ, default 0.01 (7.3.1(3))

**Formulas**
- f1 = (λ² / (2π·L²))·√((EI)_L / m)
  - Boundary dropdown: λ = π (pinned-pinned, gives 7.5), 3.927 (pinned-fixed), 4.730 (fixed-fixed), 1.875 (cantilever)
  - Optional source: the first mode of an AxisVM vibration case (`GetFrequencyParameters`)
- 7.3.3(1): if f1 ≤ 8 Hz, the floor gets "special investigation required" (status *Not covered*, not *Pass*)
- a = c·F·L³ / (EI_joist)·k_dist·k_amp ≤ a_lim   (7.3), F = 1 kN
  - c = 1/48 (pinned-pinned), with matching values for the other boundaries
  - EI_joist = (EI)_L·s
- k_dist = max{0.38 − 0.08·ln[14·(EI)_b / s⁴]; 0.30}, with (EI)_b in N·mm²/m and s in mm (UK NA form). *Custom* takes any value.
- k_amp: default 1.05 (simply supported solid timber); dropdown 1.05 / 1.10 / 1.15 / 1.30 for SS or continuous, solid or glulam (UK NA values, **VERIFY**). *Custom* takes any value.
- n40 = {[(40/f1)² − 1]·(B/L)⁴·(EI)_L / (EI)_b}^0.25   (7.7)
- v = 4·(0.4 + 0.6·n40) / (m·B·L + 200)   (7.6), in m/(Ns²)
- v ≤ b^(f1·ζ − 1)   (7.4)

**Limits a and b** (NA choice, preset dropdown):

| Preset | a_lim [mm/kN] | b |
|---|---|---|
| UK NA (default, as in the sheet) | 1.8 for L ≤ 4 m; 16500 / L^1.1 for L > 4 m (L in mm) | from fig. 7.2 relation, see below |
| EC5 fig. 7.2 | user picks a in 0.5–4 | b from the fig. 7.2 line |
| NL NA | **VERIFY**: the NB to NEN-EN 1995-1-1 is not in `docs/reference`; until the values are entered this preset is disabled | |
| Custom | any | any |

- b from a: the sheet's fit of fig. 7.2 passes through b = 150 at a = 0.5 and b ≈ 100 at a = 1.5 (UK NA form: b = 180 − 60a for a ≤ 1; b = 160 − 40a for a > 1, **VERIFY**). A dropdown chooses whether b uses a_lim or a_calc (**VERIFY** which one the NA intends).
- Utilisations: f_lim/f1 (when a minimum frequency is set), a/a_lim, v/v_lim; the vibration UC = max.

**Worked check (user's sheet).** L = 6.527 m, s = 0.6 m, B = 4.0 m, (EI)_L = 2 928 750 Nm²/m, (EI)_b = 1 458 Nm²/m, G = 0.261 + 0.850 kN/m², ζ = 0.01:

| Quantity | Value |
|---|---|
| m | 113.3 kg/m² |
| f1 | 5.93 Hz (≤ 8 Hz, so special investigation) |
| k_dist | 0.528 |
| a | 1.827 mm/kN (a_lim UK = 1.050) |
| n40 | 10.60 |
| v | 0.00856 m/(Ns²) |
| b | 87.6 (sheet fit) |
| v_lim | 0.01488 |

(s, B and (EI)_b were back-calculated from the sheet's outputs and are consistent with them.)

## 8. Floor vibration, EN 1995-1-1:2025 §9.3 (2nd generation)

From the clause text (§9.3.1 – 9.3.2.7). Scope: human-induced vibration of floors in use categories A, B, C1, C3, D (9.3.1(3)). Crowd/physical activity (9.3.3) and machinery (9.3.4) are out of scope. Single or double span floors (9.3.2.1(2)); a continuous floor is approximated as single span on the largest span (9.3.2.1(3)). Acceleration and velocity criteria apply to the whole floor of a room, not to single girders (9.3.2.2(6)), so the inputs are per *floor field* (span l, width b), shared by the joists in it.

### 8.1 Inputs (9.3.1)

- **Damping ζ** (9.3.1.1), preset by floor type:

  | Floor type | ζ |
  |---|---|
  | Joisted | 0.02 |
  | Timber-concrete, rib, slab (CLT, LVL, GLVL, GL) | 0.025 |
  | Joisted with floating floor layer | 0.03 |
  | Timber-concrete, rib, slab with floating floor layer | 0.04 |

  Or *Custom* (e.g. from EN 16929 test).
- **Mass m** (9.3.1.2), kg/m²: permanent loads + partition walls + **10 % of the characteristic imposed load**, on all bays. In the add-on: G cases + 0.1·Q (imposed categories only, not snow/wind), or user input.
- **Stiffness** (9.3.1.3): apparent stiffness incl. shear and joint slip; (EI)_L along the span and (EI)_T transverse, per m width. Non-structural layers (screed, floating floor) may add stiffness, without composite action. The flexibility of supporting beams shall be considered (see 8.3 / 8.4).
- **Walking frequency f_w** (9.3.1.4): ≥ 1.5 Hz residential, 2.0 Hz other categories; 2.5 Hz for corridors / open spaces with > 10 m free walking.

### 8.2 Floor performance level and design situation (9.3.2.1–9.3.2.2)

- f1,lim = 4·f_w   (9.12); for multi-family residential and levels I and II, f1,lim ≥ 8 Hz.
- f1 < f1,lim → **resonant**: frequency, stiffness, velocity **and** acceleration criteria.
- f1 ≥ f1,lim → **transient**: frequency, stiffness and velocity criteria.

**Table 9.1** (defaults, every cell editable):

| Level | I | II | III | IV | V | VI | VII | VIII |
|---|---|---|---|---|---|---|---|---|
| Frequency | f1 ≥ 4.5 Hz | ← | ← | ← | f1 ≥ f1,lim | ← | ← | ← |
| w_lim,max [mm] | 0.25 | 0.25 | 0.5 | 1.0 | 1.25 | 1.5 | 1.75 | 2.0 |
| w_lim [mm] | = w_lim,max | ← | ← | max{w_lim,max·3.6/l ; 0.5} ≤ w_lim,max | ← | ← | ← | ← |
| v_rms,lim [m/s] | 0.0004 | 0.0008 | 0.0012 | 0.0016 | 0.0024 | 0.0036 | 0.0042 | 0.0048 |
| a_rms,lim [m/s²] (resonant only) | 0.02 | 0.04 | 0.06 | 0.08 | n/a | n/a | n/a | n/a |

- The frequency row's split (I–IV / V–VIII) and the w_lim row's split (I–III / IV–VIII) are read from the column layout of the screenshot; the end of the w_lim formula ("≤ w_lim,max") was cut off. **VERIFY both.**
- l in the w_lim formula is the span in m. a_rms,lim of levels I–IV corresponds to response factor R = 4, 8, 12, 16.
- **Table 9.2 (NDP)**, preset dropdown *Premium / Standard (default) / Base* + use category:

  | Use category | Premium | Standard | Base |
  |---|---|---|---|
  | A multi-family | I – III | IV, V | VI, VII |
  | A single family house | I – IV | V | VI – VIII |
  | B office | I – II | III | IV, V |

  The add-on reports the best level the floor achieves and checks it against the target level chosen.

### 8.3 Fundamental frequency (9.3.2.4)

- f1 = k_e,1·k_e,2·(π / (2·l²))·√((EI)_L / m)   (9.13)
- k_e,2 = √(1 + (l/b)⁴·(EI)_T/(EI)_L)   (9.14); for a one-way spanning floor k_e,2 = 1.0 (option: "supported on 4 edges").
- k_e,1 = 1.0 single span; double span from **Table 9.4** (l2/l = 1.0 … 0.2 → 1.00, 1.09, 1.16, 1.21, 1.25, 1.28, 1.32, 1.36, 1.41, linear interpolation). The NOTE polynomial reads k_e,1 = −x³ + 1.45x² − x + 1.55 with x = l2/l (the minus sign on x³ is needed to reproduce the table; the web view drops it).
- Alternative (9.15): f1 = k_e,1·k_e,2·18 / √w_sys, with w_sys [mm] the deflection under the floor mass m, non-continuous, incl. support beams and shear. The add-on can take w_sys from an AxisVM load case (relative to the chord is *not* applied here: the beam flexibility is wanted). If w_sys already includes transverse stiffness, k_e,2 = 1.0; if it includes flexible supports, (9.16) is skipped.
- Flexible supports (9.16): f1 = 1 / √(1/f1,rigid² + 1/(3·f1,beam,1²) + 1/(3·f1,beam,2²)), where f1,beam from (9.13) or (9.15) for the supporting beam. **VERIFY** the numerator of the last term (rendered as "2" in the web view; taken as 1 by symmetry).
- Also offered: f1 from an AxisVM vibration analysis (first relevant mode).

### 8.4 Stiffness (9.3.2.5)

- w_1kN = F·l³ / (48·(EI)_L·b_ef), F = 1 kN at midspan, single span   (9.17)
- b_ef = min{0.95·l·((EI)_T/(EI)_L)^0.25 ; b}   (9.18), uniform transverse stiffness
- b_ef = min{1.075·l^0.75·((EI + 0.63·l·(EI)_T)/(EI)_L)^0.25 ; b}   (9.19), with a continuous crosswise member (strongback) of stiffness EI connected at midspan
- Flexible supports: w_1kN = 0.5·w_beam,1 + w_rigid + 0.5·w_beam,2   (9.20), with w_beam the support-beam deflection under 0.5 kN (user input or from the beam's EI and span: w = 0.5 kN·L³/(48EI) at midspan).
- Check w_1kN ≤ w_lim (Table 9.1).

### 8.5 Velocity (9.3.2.6), all floors

- I_mod,mean = 42·f_w^1.43 / f1^1.3   (9.21), Ns
- M* = m·l·b / 4   (9.23) (option m·l·b / 2: two edges, no transverse connection)
- v1,peak = k_red·I_mod,mean / (M* + 70)   (9.22), k_red = 0.7 (max 1.0)
- k_imp = max{0.48·(b/l)·((EI)_L/(EI)_T)^0.25 ; 1.0}   (9.24)
- v_tot,peak = k_imp·v1,peak   (9.26)
- η = 1.35 − 0.4·k_imp when 1.0 ≤ k_imp ≤ 1.9, else 0.59 (joisted floors); 1.35 − 0.4·k_imp when 1.0 ≤ k_imp ≤ 1.7, else 0.67 (all other floors)   (9.27)
- v_rms = v_tot,peak·(0.65 − 0.01·f1)·(1.22 − 11.0·ζ)·η   (9.25) ≤ v_rms,lim

### 8.6 Acceleration (9.3.2.7), resonant floors only

- k_res = max{0.19·(b/l)·((EI)_L/(EI)_T)^0.25 ; 1.0}   (9.29)
- a_rms = k_res·μ_res·F_dyn / (√2·2ζ·M*)   (9.28), μ_res = 0.4 (≥ 0.8 for > 10 m free walking; max 1.0), F_dyn = 50 N
- a_rms ≤ a_rms,lim

Openings (stairs) up to 15 % of the floor area and 40 % of a floor dimension may be ignored (9.3.2.3(2)).

### 8.7 Worked check (user's sheet floor, joisted, ζ = 0.02, f_w = 1.5 Hz)

l = 6.527 m, b = 4.0 m, (EI)_L = 2 928 750 Nm²/m, (EI)_T = 1 458 Nm²/m, m = 113.3 kg/m² (sheet mass, without the 10 % imposed share), k_e,1 = k_e,2 = 1:

| Quantity | Value |
|---|---|
| f1 | 5.93 Hz ≥ 4.5 Hz |
| f1,lim | 6.0 Hz, so f1 < f1,lim: resonant |
| b_ef | 0.926 m |
| w_1kN | 2.136 mm (> 2.0, fails even level VIII) |
| I_mod,mean | 7.42 Ns |
| M* | 739.5 kg |
| v1,peak | 0.00641 m/s |
| k_imp | 1.969, so η = 0.59 |
| v_tot,peak | 0.01263 m/s |
| v_rms | 0.00440 m/s |
| k_res | 1.0 |
| a_rms | 0.478 m/s² |

## 9. Output

- **Per member**: group, span/l_rep, scheme, governing combination, every check row (w, limit, UC), governing UC and status (Pass / Fail / Not covered). For floors also f1, a, v (2004) or w_1kN, R (2025).
- **Combination selector**: *Governing* or any single combination. The table, the views and the AxisVM overlay all follow it.
- **Views**:
  - plan view coloured by UC (green < 0.8, amber ≤ 1.0, red > 1.0)
  - deflection diagram of the selected member: w_G, w_creep,G, w_Q, w_creep,Q stacked along x, against the limit line (fig. 7.1 style)
  - an AxisVM overlay layer with the same colours and UC labels (show / clear)
- **Export**: CSV / clipboard.
