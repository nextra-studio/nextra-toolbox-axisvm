using System;
using AxisVM;

namespace AxisVM_plugin.Core
{
    public static class AxisVMHelper
    {
        public static void GenerateDefaultCatalog(IAxisVMModel model)
        {
            // Add C24
            try
            {
                if (model.Materials.IndexOf("C24") <= 0)
                {
                    int c24Id = model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "C24");
                    if (c24Id <= 0)
                    {
                        model.Materials.AddTimber(ENationalDesignCode.ndcEuroCode, "EuroCode", "Timber", "C24", 0xFFD2B48C, 0xFF000000, 11E6, 370E3, 370E3, 0.2, 0.2, 0.2, 5E-6, 5E-6, 5E-6, 420.0);
                    }
                }
            }
            catch { }

            // Add OSB/3
            try
            {
                if (model.Materials.IndexOf("OSB/3") <= 0)
                {
                    int osbId = model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "C22");
                    if (osbId > 0)
                    {
                        var mat = model.Materials.Item[osbId];
                        mat.Name = "OSB/3";
                        mat.ex = 4.93E6;
                        mat.ey = 1.98E6;
                        mat.ez = 1.98E6;
                        mat.Rho = 650.0;
                    }
                    else
                    {
                        model.Materials.AddTimber(ENationalDesignCode.ndcEuroCode, "EuroCode", "Timber", "OSB/3", 0xFFCD853F, 0xFF000000, 4.93E6, 1.98E6, 1.98E6, 0.2, 0.2, 0.2, 5E-6, 5E-6, 5E-6, 650.0);
                    }
                }
            }
            catch { }

            // Add Plywood
            try
            {
                if (model.Materials.IndexOf("Plywood") <= 0)
                {
                    int plyId = model.Materials.AddFromCatalog(ENationalDesignCode.ndcEuroCode, "C20");
                    if (plyId > 0)
                    {
                        var mat = model.Materials.Item[plyId];
                        mat.Name = "Plywood";
                        mat.ex = 8.1E6;
                        mat.ey = 3.0E6;
                        mat.ez = 3.0E6;
                        mat.Rho = 430.0;
                    }
                    else
                    {
                        model.Materials.AddTimber(ENationalDesignCode.ndcEuroCode, "EuroCode", "Timber", "Plywood", 0xFFDEB887, 0xFF000000, 8.1E6, 3.0E6, 3.0E6, 0.2, 0.2, 0.2, 5E-6, 5E-6, 5E-6, 430.0);
                    }
                }
            }
            catch { }

            // Add CrossSections
            try
            {
                if (model.CrossSections.IndexOf("71x146") <= 0)
                    model.CrossSections.AddRectangular("71x146", 0.071, 0.146, ECrossSectionProcess.cspOther);
            }
            catch { }

            try
            {
                if (model.CrossSections.IndexOf("146x71") <= 0)
                    model.CrossSections.AddRectangular("146x71", 0.146, 0.071, ECrossSectionProcess.cspOther);
            }
            catch { }
        }
        public static void SetLineBeamAttributes(IAxisVMModel model, int lineId, int matId, int secId, bool isRib = false)
        {
            System.Array ids = System.Array.CreateInstance(typeof(int), new int[] { 1 }, new int[] { 1 });
            ids.SetValue(lineId, 1);
            
            System.Array attrsOut;
            model.Lines.BulkGetAttr(ids, out attrsOut);
            
            if (attrsOut != null && attrsOut.Length >= 1)
            {
                RLineAttr attr = (RLineAttr)attrsOut.GetValue(1);
                attr.LineType = isRib ? ELineType.ltRib : ELineType.ltBeam;
                attr.MaterialIndex = matId;
                attr.StartCrossSectionIndex = secId;
                attr.EndCrossSectionIndex = secId;
                
                System.Array attrsIn = System.Array.CreateInstance(typeof(RLineAttr), new int[] { 1 }, new int[] { 1 });
                attrsIn.SetValue(attr, 1);
                
                model.Lines.BulkSetAttr(ids, attrsIn);
            }
            
            try { model.Members.Add(ids); } catch { }
        }

        public static void SetLineReleases(IAxisVMModel model, int lineId, bool isPin)
        {
            try
            {
                RReleases rel = new RReleases();
                rel.x = new RRelease() { ReleaseType = EReleaseType.rtRigid };
                rel.y = new RRelease() { ReleaseType = EReleaseType.rtRigid };
                rel.z = new RRelease() { ReleaseType = EReleaseType.rtRigid };
                rel.xx = new RRelease() { ReleaseType = EReleaseType.rtRigid };
                
                if (isPin)
                {
                    rel.yy = new RRelease() { ReleaseType = EReleaseType.rtHinged };
                    rel.zz = new RRelease() { ReleaseType = EReleaseType.rtHinged };
                }
                else
                {
                    rel.yy = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = 100.0 }; // 100 Nm/rad small stiffness
                    rel.zz = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = 100.0 }; // 100 Nm/rad small stiffness
                }
                
                model.Lines.Item[lineId].SetStartReleases(ref rel);
                model.Lines.Item[lineId].SetEndReleases(ref rel);
            }
            catch { }
        }
        
        public static void SetLineEndReleases(IAxisVMModel model, int lineId, bool startSemiRigid, bool endSemiRigid)
        {
            try
            {
                if (!startSemiRigid && !endSemiRigid) return; // leave as perfectly continuous

                RReleases relSemi = new RReleases();
                relSemi.x = new RRelease() { ReleaseType = EReleaseType.rtRigid };
                relSemi.y = new RRelease() { ReleaseType = EReleaseType.rtRigid };
                relSemi.z = new RRelease() { ReleaseType = EReleaseType.rtRigid };
                relSemi.xx = new RRelease() { ReleaseType = EReleaseType.rtRigid }; // Torsion rigid
                relSemi.yy = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = 1.0 }; // Y rotation semi-rigid
                relSemi.zz = new RRelease() { ReleaseType = EReleaseType.rtSemiRigid, Init = 1.0 }; // Z rotation semi-rigid

                if (startSemiRigid) model.Lines.Item[lineId].SetStartReleases(ref relSemi);
                if (endSemiRigid) model.Lines.Item[lineId].SetEndReleases(ref relSemi);
            }
            catch { }
        }
        
        public static int CreateReferenceVector(IAxisVMModel model, double vx, double vy, double vz)
        {
            RReference r = new RReference();
            r.ReferenceType = EReferenceType.rtVector;
            
            RPoint3d p1 = new RPoint3d();
            p1.x = 0; p1.y = 0; p1.z = 0;
            
            RPoint3d p2 = new RPoint3d();
            p2.x = vx; p2.y = vy; p2.z = vz;
            
            RRefVector refVector = new RRefVector();
            refVector.P1 = p1;
            refVector.P2 = p2;
            
            RRefData refData = new RRefData();
            refData.Vector = refVector;
            r.ReferenceData = refData;
            
            int refId = model.References.Add(ref r);
            return refId;
        }

        public static void SetLineSpringAttributes(IAxisVMModel model, int lineId, double nodeSpringStiffnessY, double nodeSpringStiffnessZ, double rotStiffness = 0, int refId = 0)
        {
            System.Array ids = System.Array.CreateInstance(typeof(int), new int[] { 1 }, new int[] { 1 });
            ids.SetValue(lineId, 1);
            
            System.Array attrsOut;
            model.Lines.BulkGetAttr(ids, out attrsOut);
            
            if (attrsOut != null && attrsOut.Length >= 1)
            {
                RLineAttr attr = (RLineAttr)attrsOut.GetValue(1);
                attr.LineType = ELineType.ltSpring; // Convert to spring
                
                System.Array attrsIn = System.Array.CreateInstance(typeof(RLineAttr), new int[] { 1 }, new int[] { 1 });
                attrsIn.SetValue(attr, 1);
                
                model.Lines.BulkSetAttr(ids, attrsIn);
            }
            
            try
            {
                if (refId != 0)
                {
                    model.Lines.Item[lineId].Reference = refId;
                }
                
                RStiffnesses stiff = new RStiffnesses();
                stiff.x = 1e10; // Axial (withdrawal) rigid
                stiff.y = nodeSpringStiffnessY; // Shear Y (slip)
                stiff.z = nodeSpringStiffnessZ; // Shear Z
                stiff.xx = rotStiffness;
                stiff.yy = rotStiffness;
                stiff.zz = rotStiffness;
                
                // Using sdGeometry with a Reference Vector defines the local Z-axis.
                model.Lines.Item[lineId].DefineAsSpring(ESpringDirection.sdGeometry, ref stiff);
            }
            catch { }
        }
    }
}
