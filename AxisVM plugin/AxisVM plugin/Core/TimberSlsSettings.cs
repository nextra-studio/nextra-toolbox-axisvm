using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

/*
 * =====================================================================================
 * TIMBER SLS SETTINGS - everything the user can set, with defaults, presets and persistence
 * =====================================================================================
 *
 * The classes are shown in PropertyGrids: every number has a default, most offer a dropdown of standard
 * values (NumberChoices) that stays editable, so any custom value can be typed (doc: "default + dropdown +
 * custom"). Settings are saved as JSON in %AppData%\NextraAxisVM\TimberSls: settings.json for the last used
 * settings and one file per model with the Floor / Roof members and the load-case overrides.
 * =====================================================================================
 */

namespace AxisVM_plugin.Core
{
    // ------------------------------------------------------------------
    // Editable dropdowns for numbers
    // ------------------------------------------------------------------

    /// <summary>A double property with a dropdown of standard values that still accepts any typed value.</summary>
    public abstract class NumberChoices : DoubleConverter
    {
        protected abstract double[] Values { get; }
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context) { return new StandardValuesCollection(Values); }
        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is double) return ((double)value).ToString("0.#####", CultureInfo.InvariantCulture);
            return base.ConvertTo(context, CultureInfo.InvariantCulture, value, destinationType);
        }
        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            string s = value as string;
            if (s != null) return double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
            return base.ConvertFrom(context, culture, value);
        }
    }

    public class KdefChoices : NumberChoices { protected override double[] Values { get { return new[] { 0.6, 0.8, 1.0, 1.5, 2.0, 2.25, 2.5, 3.0 }; } } }
    public class CantileverChoices : NumberChoices { protected override double[] Values { get { return new[] { 1.0, 1.5, 2.0 }; } } }
    public class DampingChoices : NumberChoices { protected override double[] Values { get { return new[] { 0.01, 0.02, 0.025, 0.03, 0.04 }; } } }
    public class KAmpChoices : NumberChoices { protected override double[] Values { get { return new[] { 1.05, 1.10, 1.15, 1.30 }; } } }
    public class WalkingChoices : NumberChoices { protected override double[] Values { get { return new[] { 1.5, 2.0, 2.5 }; } } }
    public class KRedChoices : NumberChoices { protected override double[] Values { get { return new[] { 0.7, 1.0 }; } } }
    public class MuResChoices : NumberChoices { protected override double[] Values { get { return new[] { 0.4, 0.8, 1.0 }; } } }
    public class LoadChoices : NumberChoices { protected override double[] Values { get { return new[] { 0.25, 0.5, 0.75, 1.0, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0 }; } } }
    public class SpacingChoices : NumberChoices { protected override double[] Values { get { return new[] { 0.3, 0.4, 0.5, 0.6, 0.8, 1.0, 1.2 }; } } }
    public class CreepQChoices : NumberChoices { protected override double[] Values { get { return new[] { -1.0, 0.0, 0.2, 0.3, 0.6, 0.8, 1.0 }; } } }

    // ------------------------------------------------------------------
    // Enumerations shown in the dialogs
    // ------------------------------------------------------------------

    public enum KdefSource
    {
        [Description("From the AxisVM timber member (kdef of the line)")] FromAxisVM,
        [Description("EN 1995-1-1 Table 3.2 by material and service class")] Table32,
        [Description("Custom value")] Custom
    }

    public enum DeflectionDirection
    {
        [Description("Perpendicular to the member, in the vertical plane")] Perpendicular,
        [Description("Global Z")] GlobalZ,
        [Description("Lateral (horizontal, perpendicular to the member)")] Lateral
    }

    public enum SpanMode
    {
        [Description("Whole beam: collinear lines until a support, column or non-parallel member")] WholeBeam,
        [Description("Each AxisVM line on its own")] EachLine
    }

    /// <summary>How a span is measured (doc §5.1). Auto checks all three references and keeps the critical one.</summary>
    public enum SpanType
    {
        [Description("Auto: middle, left and right relative, critical one")] Auto,
        [Description("Span: relative to both ends (middle)")] Span,
        [Description("Cantilever: root side found from the deflection")] Cantilever
    }

    public enum AxisStiffness
    {
        [Description("Auto: AxisVM timber results are at E/(1+kdef); divide by (1+kdef of the line)")] Auto,
        [Description("Off: AxisVM results are already instantaneous (E_mean)")] Off
    }

    public enum CantileverReference
    {
        [Description("Root displacement (includes back-span rotation)")] RootDisplacement,
        [Description("Root tangent (own bending only)")] RootTangent
    }

    public enum LoadSource
    {
        [Description("Auto: combinations from AxisVM load groups")] Auto,
        [Description("Existing AxisVM load combinations")] AxisVMCombinations,
        [Description("Manual: roles and ψ per load case")] Manual
    }

    public enum VibrationMethod
    {
        [Description("Off")] Off,
        [Description("EN 1995-1-1:2004 §7.3")] Ec5_2004,
        [Description("EN 1995-1-1:2025 §9.3 (2nd generation)")] En1995_2025
    }

    public enum SpanChoice
    {
        [Description("Each span on its own (continuous: auto per §9.3.2.1)")] EachSpan,
        [Description("Largest span of the Floor group")] LargestInGroup
    }

    public enum F1Source
    {
        [Description("Formula (7.5) / (9.13)")] Formula,
        [Description("AxisVM vibration analysis, mode 1")] AxisVMVibration,
        [Description("From w_sys of a load case (9.15)")] WsysLoadCase,
        [Description("Custom value")] Custom
    }

    public enum ALimMode { [Description("UK NA: 1.8 / 16500·L^-1.1")] UkNa, [Description("Custom")] Custom }

    public enum BMode
    {
        [Description("From calculated a (UK NA relation)")] FromA,
        [Description("From a_lim (UK NA relation)")] FromALim,
        [Description("Custom b")] Custom
    }

    public enum FloorType
    {
        [Description("Joisted floor (ζ 0.02)")] Joisted,
        [Description("Joisted floor with floating floor (ζ 0.03)")] JoistedFloating,
        [Description("Timber-concrete, rib or slab: CLT, LVL, GL (ζ 0.025)")] Slab,
        [Description("Timber-concrete, rib or slab with floating floor (ζ 0.04)")] SlabFloating
    }

    public enum PerformanceLevel { I, II, III, IV, V, VI, VII, VIII }

    public enum Table92Preset
    {
        [Description("(choose to set the target level)")] None,
        [Description("A multi-family: premium (III)")] MultiPremium,
        [Description("A multi-family: standard (V)")] MultiStandard,
        [Description("A multi-family: base (VII)")] MultiBase,
        [Description("A single family: premium (IV)")] SinglePremium,
        [Description("A single family: standard (V)")] SingleStandard,
        [Description("A single family: base (VIII)")] SingleBase,
        [Description("B office: premium (II)")] OfficePremium,
        [Description("B office: standard (III)")] OfficeStandard,
        [Description("B office: base (V)")] OfficeBase
    }

    public enum ModalMassEdges { [Description("Supported on four edges: m·l·b/4")] Four, [Description("Two edges, no transverse connection: m·l·b/2")] Two }

    /// <summary>Shows the [Description] of each enum value in PropertyGrids and combo boxes.</summary>
    public class DescriptionEnumConverter : EnumConverter
    {
        private readonly Type type;
        public DescriptionEnumConverter(Type type) : base(type) { this.type = type; }

        public static string Text(object value)
        {
            if (value == null) return "";
            FieldInfo f = value.GetType().GetField(value.ToString());
            DescriptionAttribute d = f == null ? null : (DescriptionAttribute)Attribute.GetCustomAttribute(f, typeof(DescriptionAttribute));
            return d != null ? d.Description : value.ToString();
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value != null && value.GetType() == type) return Text(value);
            return base.ConvertTo(context, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            string s = value as string;
            if (s != null)
                foreach (object v in Enum.GetValues(type)) if (Text(v) == s || v.ToString() == s) return v;
            return base.ConvertFrom(context, culture, value);
        }
    }

    // ------------------------------------------------------------------
    // Settings classes
    // ------------------------------------------------------------------

    /// <summary>How members are read: direction, span splitting, cantilevers.</summary>
    public class GeneralSettings
    {
        [Category("Deflection direction"), DisplayName("Direction"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("Component of the displacement that is checked. Perpendicular: normal to the member in its vertical plane (vertical for floors, perpendicular to the slope for rafters).")]
        public DeflectionDirection Direction { get; set; }

        [Category("Spans"), DisplayName("Span mode"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("Whole beam: collinear selected lines form one beam, split into spans at supports, columns and (option) non-parallel connecting members. Each line: every AxisVM line is checked as its own span.")]
        public SpanMode SpanMode { get; set; }

        [Category("Spans"), DisplayName("Default span type"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("For members without their own type (tab 1, 'Set type'). Auto checks the span relative to both ends (middle), relative to the left end and relative to the right end (cantilever, l_rep = factor × L) and keeps the critical one; the result says which was used.")]
        public SpanType DefaultType { get; set; }

        [Category("Spans"), DisplayName("Split at columns")]
        [Description("A member is split into spans at nodal supports and at nodes where a column (or another vertical line) connects. Deflection is measured per span relative to its own end points (doc §5.1).")]
        public bool SplitAtColumns { get; set; }

        [Category("Spans"), DisplayName("Split at non-parallel members")]
        [Description("Whole beam mode: also split where a non-parallel beam or rib connects (e.g. joists running over a supporting beam). Switch off when checking a girder that carries joists framing into it: those joists load it, they do not support it.")]
        public bool SplitAtBeams { get; set; }

        [Category("Spans"), DisplayName("Cantilever reference"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("Root displacement: tip minus root, includes the rotation of the back span (conservative, default). Root tangent: also removes the root rotation, so only the cantilever's own bending counts.")]
        public CantileverReference CantileverRef { get; set; }

        [Category("AxisVM results"), DisplayName("Timber stiffness in AxisVM"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("AxisVM runs the static analysis of timber members with E_mean/(1+kdef) (AxisVM manual, timber SLS). Auto divides the displacements of every timber line by (1 + its kdef) to get the instantaneous deflection at E_mean, then applies creep per load case. Choose Off when the model already uses E_mean.")]
        public AxisStiffness AxisStiffness { get; set; }

        [Category("Load combinations"), DisplayName("Max. generated combinations")]
        [Description("Cap for the Auto source; above it accompanying actions are taken all present or all absent.")]
        public int CombinationCap { get; set; }

        public GeneralSettings()
        {
            Direction = DeflectionDirection.Perpendicular;
            SplitAtColumns = true;
            SplitAtBeams = true;
            CombinationCap = 2000;
        }
    }

    /// <summary>Creep, kdef, cantilevers and precamber of one group (Floor or Roof). Limit rows are separate.</summary>
    public class GroupSettings
    {
        [Browsable(false)] public string Name { get; set; }

        [Category("1 Creep"), DisplayName("Creep model"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("EC5: 2.2.3(5), G creeps with (1+kdef), each Q with (1+ψ2·kdef) (Harff / NL and FinWood style). Conservative: the whole characteristic combination creeps (AxisVM conservative method). Custom: set the creep shares below.")]
        public CreepModel CreepModel { get; set; }

        [Category("1 Creep"), DisplayName("Custom: creep share of G"), TypeConverter(typeof(KdefChoices))]
        [Description("Custom model only: creep = share × kdef × w_G. EC5 = 1.0.")]
        public double CreepShareG { get; set; }

        [Category("1 Creep"), DisplayName("Custom: creep share of Q"), TypeConverter(typeof(CreepQChoices))]
        [Description("Custom model only: creep = share × kdef × w_Q. −1 = use ψ2 of the load case (EC5).")]
        public double CreepShareQ { get; set; }

        [Category("2 kdef"), DisplayName("kdef source"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("Where kdef comes from. From AxisVM uses the kdef of the timber line and falls back to Table 3.2.")]
        public KdefSource KdefSource { get; set; }

        [Category("2 kdef"), DisplayName("Service class")]
        [Description("Used for Table 3.2 when the line has no service class: 1, 2 or 3.")]
        public int ServiceClass { get; set; }

        [Category("2 kdef"), DisplayName("Custom kdef"), TypeConverter(typeof(KdefChoices))]
        [Description("Table 3.2: solid / glulam / LVL 0.60, 0.80, 2.00 (SC1-3); plywood 0.80 / 1.00 / 2.50; OSB/3 1.50 / 2.25; particleboard P5 2.25 / 3.00.")]
        public double KdefCustom { get; set; }

        [Category("3 Geometry"), DisplayName("Cantilever factor (l_rep = factor × L)"), TypeConverter(typeof(CantileverChoices))]
        [Description("l_rep of a cantilever. NL NA: 2.0 (twice the cantilever length).")]
        public double CantileverFactor { get; set; }

        [Category("3 Geometry"), DisplayName("Precamber w_c [mm]")]
        [Description("Precamber at midspan (cantilever: at the tip). Reduces w_net,fin and w_max, never w_bij.")]
        public double PrecamberMm { get; set; }

        [Browsable(false)] public List<LimitRow> Rows { get; set; }

        public GroupSettings()
        {
            CreepModel = CreepModel.Ec5;
            CreepShareG = 1.0;
            CreepShareQ = -1;
            KdefSource = KdefSource.FromAxisVM;
            ServiceClass = 1;
            KdefCustom = 0.6;
            CantileverFactor = 2.0;
            Rows = new List<LimitRow>();
        }
    }

    /// <summary>Floor vibration inputs (Floor group only).</summary>
    public class VibrationSettings
    {
        private FloorType floorType;
        private Table92Preset preset;

        [Category("1 Method"), DisplayName("Method"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("EN 1995-1-1:2004 §7.3.3 (f1, a, v) or EN 1995-1-1:2025 §9.3.2 (frequency, stiffness, velocity, acceleration, floor performance levels).")]
        public VibrationMethod Method { get; set; }

        [Category("1 Method"), DisplayName("Span used"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("Each span: the joist's own span (two spans in a member: double span floor; more: single span on the largest span, 9.3.2.1(3)). Largest: one span for all floor members (conservative).")]
        public SpanChoice SpanChoice { get; set; }

        [Category("2 Default floor field (members without own values)"), DisplayName("Floor width b [m]")]
        [Description("Width of the floor perpendicular to the span (distance between edge supports along the joists' row).")]
        public double Width { get; set; }

        [Category("2 Default floor field (members without own values)"), DisplayName("Joist spacing s [m]"), TypeConverter(typeof(SpacingChoices))]
        [Description("(EI)_L = E_mean·I_joist / s. For slab floors (CLT) analysed as 1 m strips, use s = strip width.")]
        public double Spacing { get; set; }

        [Category("2 Default floor field (members without own values)"), DisplayName("(EI)_T / (EI)_b transverse [Nm²/m]")]
        [Description("Bending stiffness perpendicular to the joists per metre (deck, battens), (EI)_b of EC5:2004 and (EI)_T of 2025. Apparent stiffness, 9.3.1.3.")]
        public double EiTransverse { get; set; }

        [Category("2 Default floor field (members without own values)"), DisplayName("Extra (EI)_L of the deck [Nm²/m]")]
        [Description("Optional stiffness along the span added to E·I/s, e.g. screed or floating floor (no composite action, 9.3.1.3(4)).")]
        public double EiDeckLongitudinal { get; set; }

        [Category("3 Default mass (members without own values)"), DisplayName("Permanent load incl. self-weight [kN/m²]"), TypeConverter(typeof(LoadChoices))]
        [Description("Floor self-weight, finishes, supported elements and partitions (9.3.1.2).")]
        public double PermanentLoad { get; set; }

        [Category("3 Default mass (members without own values)"), DisplayName("Imposed load q_k [kN/m²]"), TypeConverter(typeof(LoadChoices))]
        [Description("Characteristic imposed load. 2025: 10 % is added to the mass (9.3.1.2). EC5:2004: not used (7.3.3(3), unloaded floor).")]
        public double ImposedLoad { get; set; }

        [Category("3 Default mass (members without own values)"), DisplayName("Mass override [kg/m²]")]
        [Description("0 = from the loads above (·1000/9.81). Any other value is used as the floor mass m.")]
        public double MassOverride { get; set; }

        [Category("4 Frequency"), DisplayName("f1 source"), TypeConverter(typeof(DescriptionEnumConverter))]
        public F1Source F1Source { get; set; }

        [Category("4 Frequency"), DisplayName("AxisVM vibration case number")]
        [Description("Result case index of the AxisVM vibration analysis (1 = first). Mode 1 is used.")]
        public int VibrationCase { get; set; }

        [Category("4 Frequency"), DisplayName("w_sys load case name")]
        [Description("2025 (9.15): name of the load case that carries the floor mass m as a load. Its largest absolute deflection along the span is w_sys.")]
        public string WsysLoadCase { get; set; }

        [Category("4 Frequency"), DisplayName("Custom f1 [Hz]")]
        public double F1Custom { get; set; }

        [Category("4 Frequency"), DisplayName("Damping ratio ζ"), TypeConverter(typeof(DampingChoices))]
        [Description("EC5:2004 7.3.1(3): 0.01. 2025 9.3.1.1: 0.02 joisted, 0.025 slab/rib/TCC, 0.03 joisted with floating floor, 0.04 slab with floating floor. Set automatically when the floor type changes.")]
        public double Damping { get; set; }

        [Category("5 EC5:2004"), DisplayName("Boundary condition")]
        [Description("λ and the point load coefficient c: pinned-pinned π, 1/48; pinned-fixed 3.927, 7/768; fixed-fixed 4.730, 1/192.")]
        public TimberSlsMath.Boundary Boundary { get; set; }

        [Category("5 EC5:2004"), DisplayName("k_dist custom (0 = auto)")]
        [Description("0: k_dist = max{0.38 − 0.08·ln[14(EI)_b/s⁴]; 0.30} (UK NA). Otherwise the value typed.")]
        public double KDistCustom { get; set; }

        [Category("5 EC5:2004"), DisplayName("k_amp"), TypeConverter(typeof(KAmpChoices))]
        [Description("UK NA: 1.05 simply supported solid timber, 1.10 continuous solid timber, 1.15 simply supported glulam, 1.30 continuous glulam (VERIFY against your NA).")]
        public double KAmp { get; set; }

        [Category("5 EC5:2004"), DisplayName("a_lim"), TypeConverter(typeof(DescriptionEnumConverter))]
        public ALimMode ALimMode { get; set; }

        [Category("5 EC5:2004"), DisplayName("a_lim custom [mm/kN]")]
        public double ALimCustom { get; set; }

        [Category("5 EC5:2004"), DisplayName("b"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("b of v ≤ b^(f1·ζ−1). UK NA relation: b = 180 − 60a (a ≤ 1), 160 − 40a (a > 1).")]
        public BMode BMode { get; set; }

        [Category("5 EC5:2004"), DisplayName("b custom")]
        public double BCustom { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Target performance level")]
        [Description("Table 9.1 level agreed with the client. The result also shows the best level the floor achieves.")]
        public PerformanceLevel TargetLevel { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Table 9.2 preset"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("Sets the target level from the use category and choice (Table 9.2, NDP). Standard is the default choice for floors; the lowest level of each range is taken.")]
        public Table92Preset Table92
        {
            get { return preset; }
            set
            {
                preset = value;
                switch (value)
                {
                    case Table92Preset.MultiPremium: TargetLevel = PerformanceLevel.III; MultiFamily = true; break;
                    case Table92Preset.MultiStandard: TargetLevel = PerformanceLevel.V; MultiFamily = true; break;
                    case Table92Preset.MultiBase: TargetLevel = PerformanceLevel.VII; MultiFamily = true; break;
                    case Table92Preset.SinglePremium: TargetLevel = PerformanceLevel.IV; MultiFamily = false; break;
                    case Table92Preset.SingleStandard: TargetLevel = PerformanceLevel.V; MultiFamily = false; break;
                    case Table92Preset.SingleBase: TargetLevel = PerformanceLevel.VIII; MultiFamily = false; break;
                    case Table92Preset.OfficePremium: TargetLevel = PerformanceLevel.II; MultiFamily = false; WalkingFrequency = 2.0; break;
                    case Table92Preset.OfficeStandard: TargetLevel = PerformanceLevel.III; MultiFamily = false; WalkingFrequency = 2.0; break;
                    case Table92Preset.OfficeBase: TargetLevel = PerformanceLevel.V; MultiFamily = false; WalkingFrequency = 2.0; break;
                }
            }
        }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Floor type"), TypeConverter(typeof(DescriptionEnumConverter))]
        [Description("Sets ζ (9.3.1.1) and η of (9.27): joisted floors 1.9 / 0.59, other floors 1.7 / 0.67.")]
        public FloorType FloorType
        {
            get { return floorType; }
            set
            {
                floorType = value;
                switch (value)
                {
                    case FloorType.Joisted: Damping = 0.02; break;
                    case FloorType.JoistedFloating: Damping = 0.03; break;
                    case FloorType.Slab: Damping = 0.025; break;
                    default: Damping = 0.04; break;
                }
            }
        }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Walking frequency f_w [Hz]"), TypeConverter(typeof(WalkingChoices))]
        [Description("9.3.1.4: at least 1.5 Hz residential, 2.0 Hz other categories, 2.5 Hz for > 10 m free walking. f1,lim = 4·f_w (9.12).")]
        public double WalkingFrequency { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Multi-family residential")]
        [Description("f1,lim not less than 8 Hz (9.3.2.1(7)); also applied for levels I and II.")]
        public bool MultiFamily { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Two-way spanning (k_e,2 from 9.14)")]
        [Description("Off: one-way spanning floor, k_e,2 = 1.0.")]
        public bool TwoWay { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Modal mass M*"), TypeConverter(typeof(DescriptionEnumConverter))]
        public ModalMassEdges ModalMass { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("Crosswise stiffener EI [Nm²] (0 = none)")]
        [Description("Continuous crosswise member at midspan, mechanically connected: b_ef from (9.19).")]
        public double EiStiffener { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("f1 support beam 1 [Hz] (0 = rigid)")]
        [Description("(9.16): frequency of a flexible supporting beam at one end.")]
        public double F1Beam1 { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("f1 support beam 2 [Hz] (0 = rigid)")]
        public double F1Beam2 { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("w support beam 1 under 0.5 kN [mm]")]
        [Description("(9.20): w_1kN = 0.5·w_beam,1 + w_rigid + 0.5·w_beam,2.")]
        public double WBeam1 { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("w support beam 2 under 0.5 kN [mm]")]
        public double WBeam2 { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("k_red"), TypeConverter(typeof(KRedChoices))]
        [Description("(9.22): 0.7; 1.0 when walker and receiver are at the same location.")]
        public double KRed { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("μ_res"), TypeConverter(typeof(MuResChoices))]
        [Description("(9.28): 0.4; at least 0.8 for > 10 m free walking; 1.0 full resonance at midspan.")]
        public double MuRes { get; set; }

        [Category("6 EN 1995-1-1:2025"), DisplayName("F_dyn [N]")]
        public double FDyn { get; set; }

        [Category("7 Table 9.1 (editable)"), DisplayName("w_lim,max I..VIII [mm]")]
        public string WLimMax { get; set; }

        [Category("7 Table 9.1 (editable)"), DisplayName("v_rms,lim I..VIII [m/s]")]
        public string VRmsLim { get; set; }

        [Category("7 Table 9.1 (editable)"), DisplayName("a_rms,lim I..IV [m/s²]")]
        public string ARmsLim { get; set; }

        [Category("7 Table 9.1 (editable)"), DisplayName("f1 minimum levels I..IV [Hz]")]
        public double F1Min { get; set; }

        public VibrationSettings()
        {
            Method = VibrationMethod.Off;
            Width = 4.0;
            Spacing = 0.6;
            EiTransverse = 1500;
            PermanentLoad = 0.5;
            ImposedLoad = 1.75;
            VibrationCase = 1;
            WsysLoadCase = "";
            FloorType = FloorType.Joisted;   // sets ζ = 0.02
            Boundary = TimberSlsMath.Boundary.PinnedPinned;
            KAmp = 1.05;
            ALimCustom = 1.5;
            BCustom = 100;
            TargetLevel = PerformanceLevel.V;
            WalkingFrequency = 1.5;
            MultiFamily = true;
            KRed = 0.7;
            MuRes = 0.4;
            FDyn = 50;
            TimberSlsMath.Table91 t = new TimberSlsMath.Table91();
            WLimMax = Join(t.WLimMax);
            VRmsLim = Join(t.VRmsLim);
            ARmsLim = Join(t.ARmsLim.Take(4).ToArray());
            F1Min = t.F1Min;
        }

        /// <summary>Table 9.1 from the editable strings; bad entries keep the default.</summary>
        public TimberSlsMath.Table91 BuildTable()
        {
            TimberSlsMath.Table91 t = new TimberSlsMath.Table91();
            Parse(WLimMax, t.WLimMax);
            Parse(VRmsLim, t.VRmsLim);
            double[] a = new double[4];
            Array.Copy(t.ARmsLim, a, 4);
            Parse(ARmsLim, a);
            Array.Copy(a, t.ARmsLim, 4);
            t.F1Min = F1Min > 0 ? F1Min : 4.5;
            return t;
        }

        private static string Join(double[] v) { return string.Join("; ", v.Select(x => x.ToString("0.####", CultureInfo.InvariantCulture))); }

        private static void Parse(string s, double[] into)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            string[] parts = s.Split(new[] { ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < into.Length; i++)
            {
                double v;
                if (double.TryParse(parts[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) into[i] = v;
            }
        }
    }

    public enum ColourScale { [Description("Gradient (continuous)")] Gradient, [Description("Steps (0.5 / 0.8 / 1.0 / 1.2)")] Steps }

    /// <summary>How results are drawn: in the window, on the AxisVM model and in the exported report.</summary>
    public class DisplaySettings
    {
        public ColourScale Scale = ColourScale.Gradient;
        public double LineWidth = 5;      // px in the window; AxisVM pen width = half of it
        public double LabelSize = 9;      // pt in the window; AxisVM text height scales with it
        public double LegendMax = 1.5;    // top of the gradient legend (UC)
        public bool Labels = true;
    }

    /// <summary>
    /// Floor field of one member for vibration (doc §7, §8). NaN = use the default of the Vibration tab.
    /// Stored per AxisVM line id; a span takes the first of its lines that has one.
    /// </summary>
    public class VibOverride
    {
        public int Line;
        public double Width = double.NaN, Spacing = double.NaN, EiT = double.NaN, EiDeckL = double.NaN;
        public double PermanentLoad = double.NaN, ImposedLoad = double.NaN, Mass = double.NaN;

        public bool IsEmpty
        {
            get { return new[] { Width, Spacing, EiT, EiDeckL, PermanentLoad, ImposedLoad, Mass }.All(double.IsNaN); }
        }

        public static double Or(double v, double fallback) { return double.IsNaN(v) ? fallback : v; }
    }

    /// <summary>Per load case overrides (role, group, ψ), stored by load case name.</summary>
    public class CaseOverride
    {
        public string Name;
        public LoadRole Role;
        public string Group;
        public bool Exclusive;
        public double Psi0, Psi1, Psi2;
        public bool Imposed;
    }

    /// <summary>Everything of one session: general, both groups, vibration, loads and members.</summary>
    public class TimberSlsSettings
    {
        public GeneralSettings General = new GeneralSettings();
        public GroupSettings Floor = new GroupSettings { Name = "Floor" };
        public GroupSettings Roof = new GroupSettings { Name = "Roof" };
        public VibrationSettings Vibration = new VibrationSettings();
        public LoadSource LoadSource = LoadSource.Auto;
        public List<string> SelectedCombinations = new List<string>();
        public List<CaseOverride> Cases = new List<CaseOverride>();
        public List<int> FloorLines = new List<int>();
        public List<int> RoofLines = new List<int>();
        public Dictionary<int, SpanType> LineTypes = new Dictionary<int, SpanType>();
        public Dictionary<int, VibOverride> VibOverrides = new Dictionary<int, VibOverride>();
        public DisplaySettings Display = new DisplaySettings();

        /// <summary>The vibration override of a span: the first of its lines that has one, else null.</summary>
        public VibOverride VibFor(IEnumerable<int> lines)
        {
            foreach (int l in lines) { VibOverride o; if (VibOverrides.TryGetValue(l, out o) && !o.IsEmpty) return o; }
            return null;
        }

        /// <summary>Type of a span: its lines' own type when they agree, else the default.</summary>
        public SpanType TypeOf(IEnumerable<int> lines)
        {
            List<SpanType> t = lines.Select(l => LineTypes.ContainsKey(l) ? LineTypes[l] : General.DefaultType).Distinct().ToList();
            return t.Count == 1 ? t[0] : General.DefaultType;
        }

        public TimberSlsSettings()
        {
            Floor.Rows = TimberSlsPresets.Rows(TimberSlsPresets.Names[0]);
            Roof.Rows = TimberSlsPresets.Rows(TimberSlsPresets.Names[2]);
        }

        public GroupSettings Group(string name) { return name == "Roof" ? Roof : Floor; }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        private static string Folder
        {
            get
            {
                // Live checks point this elsewhere so they never touch the user's saved settings
                string over = Environment.GetEnvironmentVariable("NEXTRA_TIMBERSLS_DIR");
                return !string.IsNullOrEmpty(over) ? over : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextraAxisVM", "TimberSls");
            }
        }

        private static string ModelFile(string modelPath)
        {
            string key = string.IsNullOrEmpty(modelPath) ? "unsaved" : modelPath.ToLowerInvariant();
            int h = 17;
            foreach (char c in key) h = unchecked(h * 31 + c);
            string name = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(modelPath) ? "unsaved" : modelPath);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(Folder, "models", name + "-" + ((uint)h).ToString("x8") + ".json");
        }

        /// <summary>Last used settings, with this model's members and load-case overrides; defaults when missing.</summary>
        public static TimberSlsSettings Load(string modelPath)
        {
            TimberSlsSettings s = new TimberSlsSettings();
            try
            {
                string f = Path.Combine(Folder, "settings.json");
                if (File.Exists(f)) s.ReadCommon(JsonValue.Parse(File.ReadAllText(f)));
                string m = ModelFile(modelPath);
                if (File.Exists(m)) s.ReadModel(JsonValue.Parse(File.ReadAllText(m)));
            }
            catch { }
            return s;
        }

        /// <summary>Saves; returns null or an error message.</summary>
        public string Save(string modelPath)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Folder, "models"));
                File.WriteAllText(Path.Combine(Folder, "settings.json"), WriteCommon().ToJson());
                File.WriteAllText(ModelFile(modelPath), WriteModel().ToJson());
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private JsonValue WriteCommon()
        {
            JsonValue o = JsonValue.Obj();
            o.Set("general", Props(General));
            o.Set("floor", GroupJson(Floor));
            o.Set("roof", GroupJson(Roof));
            o.Set("vibration", Props(Vibration));
            o.Set("loadSource", JsonValue.Str(LoadSource.ToString()));
            o.Set("display", JsonValue.Obj().Set("scale", JsonValue.Str(Display.Scale.ToString())).Set("lineWidth", JsonValue.Num(Display.LineWidth))
                .Set("labelSize", JsonValue.Num(Display.LabelSize)).Set("legendMax", JsonValue.Num(Display.LegendMax))
                .Set("labels", JsonValue.Bool(Display.Labels)));
            return o;
        }

        private void ReadCommon(JsonValue o)
        {
            Fill(General, G(o, "general"));
            ReadGroup(Floor, G(o, "floor"));
            ReadGroup(Roof, G(o, "roof"));
            Fill(Vibration, G(o, "vibration"));
            LoadSource ls;
            if (Enum.TryParse(G(o, "loadSource").AsString(""), out ls)) LoadSource = ls;
            JsonValue d = G(o, "display");
            ColourScale cs;
            if (Enum.TryParse(G(d, "scale").AsString(""), out cs)) Display.Scale = cs;
            Display.LineWidth = Math.Max(1, Math.Min(20, G(d, "lineWidth").AsDouble(Display.LineWidth)));
            Display.LabelSize = Math.Max(5, Math.Min(30, G(d, "labelSize").AsDouble(Display.LabelSize)));
            Display.LegendMax = Math.Max(1.0, Math.Min(5, G(d, "legendMax").AsDouble(Display.LegendMax)));
            if (G(d, "labels").Type == JsonValue.JsonType.Bool) Display.Labels = G(d, "labels").BoolValue;
        }

        private JsonValue WriteModel()
        {
            JsonValue o = JsonValue.Obj();
            JsonValue fl = JsonValue.Arr(), rl = JsonValue.Arr(), cs = JsonValue.Arr(), sc = JsonValue.Arr();
            foreach (int i in FloorLines) fl.Add(JsonValue.Num(i));
            foreach (int i in RoofLines) rl.Add(JsonValue.Num(i));
            foreach (string n in SelectedCombinations) sc.Add(JsonValue.Str(n));
            foreach (CaseOverride c in Cases)
            {
                JsonValue j = JsonValue.Obj();
                j.Set("name", JsonValue.Str(c.Name)).Set("role", JsonValue.Str(c.Role.ToString())).Set("group", JsonValue.Str(c.Group ?? ""))
                 .Set("exclusive", JsonValue.Bool(c.Exclusive)).Set("psi0", JsonValue.Num(c.Psi0)).Set("psi1", JsonValue.Num(c.Psi1))
                 .Set("psi2", JsonValue.Num(c.Psi2)).Set("imposed", JsonValue.Bool(c.Imposed));
                cs.Add(j);
            }
            JsonValue types = JsonValue.Obj();
            foreach (var kv in LineTypes) types.Set(kv.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Str(kv.Value.ToString()));
            JsonValue vibs = JsonValue.Arr();
            foreach (VibOverride v in VibOverrides.Values.Where(x => !x.IsEmpty))
            {
                JsonValue j = JsonValue.Obj().Set("line", JsonValue.Num(v.Line));
                Action<string, double> put = (k, x) => { if (!double.IsNaN(x)) j.Set(k, JsonValue.Num(x)); };
                put("b", v.Width); put("s", v.Spacing); put("eiT", v.EiT); put("eiDeckL", v.EiDeckL); put("g", v.PermanentLoad); put("q", v.ImposedLoad); put("m", v.Mass);
                vibs.Add(j);
            }
            o.Set("floorLines", fl).Set("roofLines", rl).Set("cases", cs).Set("combinations", sc).Set("lineTypes", types).Set("vibration", vibs);
            return o;
        }

        private void ReadModel(JsonValue o)
        {
            FloorLines = Ints(G(o, "floorLines"));
            RoofLines = Ints(G(o, "roofLines"));
            VibOverrides.Clear();
            JsonValue vb = G(o, "vibration");
            for (int i = 0; i < vb.Count; i++)
            {
                JsonValue j = vb.At(i);
                VibOverride v = new VibOverride
                {
                    Line = G(j, "line").AsInt(0), Width = G(j, "b").AsDouble(double.NaN), Spacing = G(j, "s").AsDouble(double.NaN),
                    EiT = G(j, "eiT").AsDouble(double.NaN), EiDeckL = G(j, "eiDeckL").AsDouble(double.NaN), PermanentLoad = G(j, "g").AsDouble(double.NaN),
                    ImposedLoad = G(j, "q").AsDouble(double.NaN), Mass = G(j, "m").AsDouble(double.NaN)
                };
                if (v.Line > 0 && !v.IsEmpty) VibOverrides[v.Line] = v;
            }
            LineTypes.Clear();
            JsonValue lt = G(o, "lineTypes");
            if (lt.Type == JsonValue.JsonType.Object)
                foreach (var kv in lt.ObjectValue)
                {
                    int id; SpanType t;
                    if (int.TryParse(kv.Key, out id) && Enum.TryParse(kv.Value.AsString(""), out t)) LineTypes[id] = t;
                }
            SelectedCombinations.Clear();
            JsonValue sc = G(o, "combinations");
            for (int i = 0; i < sc.Count; i++) SelectedCombinations.Add(sc.At(i).AsString(""));
            Cases.Clear();
            JsonValue cs = G(o, "cases");
            for (int i = 0; i < cs.Count; i++)
            {
                JsonValue j = cs.At(i);
                LoadRole role;
                Enum.TryParse(G(j, "role").AsString("Ignore"), out role);
                Cases.Add(new CaseOverride
                {
                    Name = G(j, "name").AsString(""), Role = role, Group = G(j, "group").AsString(""),
                    Exclusive = G(j, "exclusive").Type == JsonValue.JsonType.Bool && G(j, "exclusive").BoolValue,
                    Psi0 = G(j, "psi0").AsDouble(0), Psi1 = G(j, "psi1").AsDouble(0), Psi2 = G(j, "psi2").AsDouble(0),
                    Imposed = G(j, "imposed").Type == JsonValue.JsonType.Bool && G(j, "imposed").BoolValue
                });
            }
        }

        /// <summary>Member of an object, or a null JSON value when the object or the member is missing.</summary>
        private static JsonValue G(JsonValue o, string key)
        {
            JsonValue v = o == null ? null : o[key];
            return v ?? new JsonValue();
        }

        private static List<int> Ints(JsonValue a)
        {
            List<int> r = new List<int>();
            for (int i = 0; i < a.Count; i++) r.Add(a.At(i).AsInt(0));
            return r;
        }

        private static JsonValue GroupJson(GroupSettings g)
        {
            JsonValue o = Props(g);
            JsonValue rows = JsonValue.Arr();
            foreach (LimitRow r in g.Rows)
            {
                JsonValue j = JsonValue.Obj();
                j.Set("enabled", JsonValue.Bool(r.Enabled)).Set("quantity", JsonValue.Str(r.Quantity.ToString()))
                 .Set("ratio", JsonValue.Num(r.Ratio)).Set("abs", JsonValue.Num(r.AbsMm));
                rows.Add(j);
            }
            o.Set("rows", rows);
            return o;
        }

        private static void ReadGroup(GroupSettings g, JsonValue o)
        {
            if (o == null || o.IsNull) return;
            Fill(g, o);
            JsonValue rows = G(o, "rows");
            if (rows.Count == 0) return;
            g.Rows = new List<LimitRow>();
            for (int i = 0; i < rows.Count; i++)
            {
                JsonValue j = rows.At(i);
                SlsQuantity q;
                if (!Enum.TryParse(G(j, "quantity").AsString(""), out q)) continue;
                g.Rows.Add(new LimitRow(q, G(j, "ratio").AsDouble(0), G(j, "abs").AsDouble(0))
                {
                    Enabled = G(j, "enabled").Type != JsonValue.JsonType.Bool || G(j, "enabled").BoolValue
                });
            }
        }

        /// <summary>Public read/write properties of simple types (number, bool, enum, string) as a JSON object.</summary>
        private static JsonValue Props(object o)
        {
            JsonValue j = JsonValue.Obj();
            foreach (PropertyInfo p in o.GetType().GetProperties())
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                object v = p.GetValue(o, null);
                Type t = p.PropertyType;
                if (t == typeof(double)) { double d = (double)v; if (!double.IsNaN(d) && !double.IsInfinity(d)) j.Set(p.Name, JsonValue.Num(d)); }
                else if (t == typeof(int)) j.Set(p.Name, JsonValue.Num((int)v));
                else if (t == typeof(bool)) j.Set(p.Name, JsonValue.Bool((bool)v));
                else if (t.IsEnum || t == typeof(string)) j.Set(p.Name, JsonValue.Str(v == null ? "" : v.ToString()));
            }
            return j;
        }

        /// <summary>The reverse of Props; unknown or bad values keep the default. Table92 is skipped (it sets other values).</summary>
        private static void Fill(object o, JsonValue j)
        {
            if (j == null || j.Type != JsonValue.JsonType.Object) return;
            // Damping last: the FloorType setter resets it, so a saved custom ζ must be applied after FloorType
            foreach (PropertyInfo p in o.GetType().GetProperties().OrderBy(x => x.Name == "Damping" ? 1 : 0))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0 || p.Name == "Table92") continue;
                JsonValue v = j[p.Name];
                if (v == null || v.IsNull) continue;
                try
                {
                    Type t = p.PropertyType;
                    if (t == typeof(double) && v.Type == JsonValue.JsonType.Number) p.SetValue(o, v.NumberValue, null);
                    else if (t == typeof(int) && v.Type == JsonValue.JsonType.Number) p.SetValue(o, (int)Math.Round(v.NumberValue), null);
                    else if (t == typeof(bool) && v.Type == JsonValue.JsonType.Bool) p.SetValue(o, v.BoolValue, null);
                    else if (t == typeof(string)) p.SetValue(o, v.AsString(""), null);
                    else if (t.IsEnum) p.SetValue(o, Enum.Parse(t, v.AsString("")), null);
                }
                catch { }
            }
        }
    }

    /// <summary>Limit row presets (doc §5.2).</summary>
    public static class TimberSlsPresets
    {
        public static readonly string[] Names =
        {
            "NL NA / Harff, floor: w_eind ≤ l/250, w_bij ≤ 3/1000·l",
            "NL NA / Harff, floor with brittle partitions: w_eind ≤ l/250, w_bij ≤ l/500",
            "NL NA / Harff, roof used by people: w_eind ≤ l/250, w_bij ≤ 3/1000·l",
            "NL NA / Harff, other roof: w_eind ≤ l/250, w_bij ≤ l/250",
            "NL NA, beams supporting walls: w_bij ≤ 15 mm (+ floor rows)",
            "NL NA, appearance: w_max ≤ l/250 (+ floor rows)",
            "EC5 Table 7.2, lenient: w_inst l/300, w_net,fin l/250, w_fin l/150",
            "EC5 Table 7.2, strict: w_inst l/500, w_net,fin l/350, w_fin l/300",
            "AxisVM timber dialog style: u_fin l/250, u_bij l/333"
        };

        public static List<LimitRow> Rows(string name)
        {
            List<LimitRow> r = new List<LimitRow>();
            int i = Array.IndexOf(Names, name);
            const double bij = 1000.0 / 3.0;   // 3/1000·l = l/333.3
            switch (i)
            {
                case 0: case 2: r.Add(new LimitRow(SlsQuantity.WnetFin, 250, 0)); r.Add(new LimitRow(SlsQuantity.Wbij, bij, 0)); break;
                case 1: r.Add(new LimitRow(SlsQuantity.WnetFin, 250, 0)); r.Add(new LimitRow(SlsQuantity.Wbij, 500, 0)); break;
                case 3: r.Add(new LimitRow(SlsQuantity.WnetFin, 250, 0)); r.Add(new LimitRow(SlsQuantity.Wbij, 250, 0)); break;
                case 4: r.Add(new LimitRow(SlsQuantity.WnetFin, 250, 0)); r.Add(new LimitRow(SlsQuantity.Wbij, bij, 15)); break;
                case 5: r.Add(new LimitRow(SlsQuantity.WnetFin, 250, 0)); r.Add(new LimitRow(SlsQuantity.Wbij, bij, 0)); r.Add(new LimitRow(SlsQuantity.WqpFin, 250, 0)); break;
                case 6: r.Add(new LimitRow(SlsQuantity.Winst, 300, 0)); r.Add(new LimitRow(SlsQuantity.WnetFin, 250, 0)); r.Add(new LimitRow(SlsQuantity.Wfin, 150, 0)); break;
                case 7: r.Add(new LimitRow(SlsQuantity.Winst, 500, 0)); r.Add(new LimitRow(SlsQuantity.WnetFin, 350, 0)); r.Add(new LimitRow(SlsQuantity.Wfin, 300, 0)); break;
                default: r.Add(new LimitRow(SlsQuantity.Wfin, 250, 0)); r.Add(new LimitRow(SlsQuantity.Wbij, 333, 0)); break;
            }
            return r;
        }
    }
}
