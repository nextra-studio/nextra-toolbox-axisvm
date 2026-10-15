using System;
using System.Collections.Generic;
using System.Globalization;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// Turns a stiffness magnitude into an <c>IAxisVMSpringParams</c> index, creating a linear,
    /// stiffness-only spring characteristic the first time each value is seen and caching it.
    ///
    /// This exists so releases, line springs and nodal supports can all be written through the
    /// versioned COM API (RReleases_V161 / RSpringCharacteristics / RNodalSupportSpringParams) instead
    /// of the obsolete records. The obsolete <c>AddNodalGlobal(RStiffnesses, RNonLinearity, ...)</c>
    /// path forces every component to be non-linear (RNonLinearity has no "linear" value), which is
    /// what makes AxisVM show a default resistance (F_xR) and the large rigid stiffness, and leaves the
    /// warping (e_w) slot in an indeterminate state. A linear spring characteristic has none of that.
    /// </summary>
    public class SpringParamResolver
    {
        private readonly IAxisVMModel model;
        private readonly List<string> notes;
        private readonly Dictionary<string, int> cache = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool warned;
        public int Created { get; private set; }

        public SpringParamResolver(IAxisVMModel model, List<string> notes)
        {
            this.model = model;
            this.notes = notes;
        }

        /// <summary>Spring-param index for a translational stiffness [kN/m], or 0 for "no stiffness".</summary>
        public int Translational(double k) { return Resolve(k, true, false); }

        /// <summary>Spring-param index for a rotational stiffness [kNm/rad], or 0 for "no stiffness".</summary>
        public int Rotational(double k) { return Resolve(k, false, false); }

        /// <summary>Line-type (sptLine) characteristic, e.g. for edge connections [kN/m/m].</summary>
        public int LineTranslational(double k) { return Resolve(k, true, true); }

        /// <summary>Line-type (sptLine) rotational characteristic [kNm/rad/m].</summary>
        public int LineRotational(double k) { return Resolve(k, false, true); }

        private int Resolve(double k, bool translational, bool line)
        {
            if (double.IsNaN(k) || double.IsInfinity(k) || k <= 0.0) return 0;

            string key = (line ? "L" : "N") + (translational ? "T:" : "R:") + k.ToString("R", CultureInfo.InvariantCulture);
            int idx;
            if (cache.TryGetValue(key, out idx)) return idx;

            // Round-trip formatting: two different stiffnesses never share a name, and so never share
            // a characteristic through IndexOfName.
            string name = string.Format(CultureInfo.InvariantCulture, "AxI {0} {1} {2}",
                line ? "L" : "N", translational ? "T" : "R", k.ToString("R", CultureInfo.InvariantCulture));

            try
            {
                int existing = model.SpringParams.IndexOfName(name);
                if (existing > 0) { cache[key] = existing; return existing; }
            }
            catch { }

            try
            {
                RSpringParam_V161 rec = new RSpringParam_V161();
                rec.SpringType = line ? ESpringParType.sptLine : ESpringParType.sptNodal;
                rec.NNType = ESpringParNNType.spnntLinear;
                rec.DOFType = translational ? ESpringParDOFType.spdoftTranslation : ESpringParDOFType.spdoftRotation;
                rec.NLESimplified = ELongBoolean.lbFalse;
                rec.K = k;
                rec.KVib = k;
                rec.DampingType = ESpringParDampingType.spdtKelvin;
                rec.C = 0.0;
                rec.NonLinearity = ESpringParNonLinearity.spnlTensionAndCompression;
                rec.NLDefType = ESpringParNLDefType.spnldtByParam;

                idx = model.SpringParams.Add_V161(name, ref rec);
            }
            catch (Exception ex)
            {
                Warn(ex.Message);
                idx = 0;
            }

            if (idx <= 0) { Warn(null); idx = 0; }
            else Created++;

            cache[key] = idx;
            return idx;
        }

        private void Warn(string detail)
        {
            if (warned) return;
            warned = true;
            notes.Add("A spring characteristic could not be created" +
                (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")") +
                "; the affected stiffness DOFs were left free.");
        }
    }
}
