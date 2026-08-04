using System;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.Functions
{
    public static class ReactionTransferFunction
    {
        public static int Execute(IAxisVMApplication axisApp)
        {
            try
            {
                // Ensure a model is open
                if (axisApp.Models.Count == 0)
                {
                    MessageBox.Show("Please open a model first.", "Reaction Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                IAxisVMModel currentModel = axisApp.Models.Item[1];
                
                // Open UI Form
                using (ReactionTransferForm form = new ReactionTransferForm())
                {
                    // Assign main window handle if possible
                    // form.Owner = ... 

                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        // Validate inputs
                        if (string.IsNullOrWhiteSpace(form.txtFilePath.Text))
                        {
                            MessageBox.Show("Please specify a target file path.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return 0;
                        }

                        // Collect user settings
                        ReactionTransferSettings settings = new ReactionTransferSettings
                        {
                            TargetFilePath = form.txtFilePath.Text,
                            IsNewFile = form.rbNewFile.Checked,
                            
                            TransferSelectedElements = form.rbTransferSelectedElements.Checked,
                            TransferParts = form.rbTransferParts.Checked,
                            
                            TransferSupports = form.chkTransferSupports.Checked,
                            TransferBottomBeams = form.chkTransferBottomBeams.Checked,
                            TransferLoadCases = form.chkTransferLoadCases.Checked,
                            TransferLoadGroups = form.chkTransferLoadGroups.Checked,
                            TransferLoadCombinations = form.chkTransferLoadCombinations.Checked,
                            
                            MergeLoads = form.rbMergeLoads.Checked,
                            ReplaceLoads = form.rbReplaceLoads.Checked,
                            
                            ApplyAsNodePointLoads = form.rbNodePointLoads.Checked,
                            ApplyAsBeamPointLoads = form.rbBeamPointLoads.Checked,
                            TidyUpLines = form.chkTidyUpLines.Checked
                        };

                        // Execute Logic
                        bool success = ReactionTransferLogic.ExecuteTransfer(axisApp, currentModel, settings);
                        
                        if (success)
                        {
                            MessageBox.Show("Reaction transfer completed successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
                
                return 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred during Reaction Transfer:\n\n{ex.Message}\n{ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return -1;
            }
        }
    }
}
