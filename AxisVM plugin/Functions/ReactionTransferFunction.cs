using System;
using System.Windows.Forms;
using AxisVM;
using AxisVM_plugin.UI;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Function wrapper that handles the UI interaction for the Reaction Transfer addon.
    /// It validates the active model, shows the settings form, and passes the settings to the core logic.
    /// </summary>
    public static class ReactionTransferFunction
    {
        public static int Execute(IAxisVMApplication axisApp)
        {
            try
            {
                // Ensure that at least one model is open in AxisVM before running the tool
                if (axisApp.Models.Count == 0)
                {
                    MessageBox.Show("Please open a model first.", "Reaction Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0; // Return 0 indicating clean exit without executing
                }

                // Get the currently active model (Item[1] is typically the active model in the COM API)
                IAxisVMModel currentModel = axisApp.Models.Item[1];
                
                // Open the user interface form to collect settings for the transfer
                using (ReactionTransferForm form = new ReactionTransferForm())
                {
                    // ShowDialog blocks execution until the user clicks OK or Cancel
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        // Validate inputs: Ensure the user provided a file path for the target model
                        if (string.IsNullOrWhiteSpace(form.txtFilePath.Text))
                        {
                            MessageBox.Show("Please specify a target file path.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return 0;
                        }

                        // Collect user settings from the form into a strongly-typed settings object
                        ReactionTransferSettings settings = new ReactionTransferSettings
                        {
                            TargetFilePath = form.txtFilePath.Text, // Where to save or load the target file
                            IsNewFile = form.rbNewFile.Checked,     // True if we are creating a brand new model
                            
                            TransferSelectedElements = true,
                            TransferParts = false,
                            
                            TransferSupports = form.chkTransferSupports.Checked,
                            TransferBottomBeams = true,
                            TransferLoadCases = true,
                            TransferLoadGroups = true,
                            TransferLoadCombinations = true,
                            
                            MergeLoads = true,
                            ReplaceLoads = false,
                            
                            ResultType = form.cbResultType.SelectedIndex, // 0 = Linear Static, 1 = NonLinear Static, 2 = Envelope
                            
                            ApplyAsNodePointLoads = form.rbNodePointLoads.Checked, // Apply reactions directly to nodes
                            ApplyAsBeamPointLoads = form.rbBeamPointLoads.Checked, // Apply reactions as point loads on lines
                            TidyUpLines = form.chkTidyUpLines.Checked              // Simplify collinear lines after transfer
                        };

                        // Execute the core business logic to perform the actual transfer
                        bool success = ReactionTransferLogic.ExecuteTransfer(axisApp, currentModel, settings);
                        
                        // Notify the user if the transfer completed successfully
                        if (success)
                        {
                            MessageBox.Show("Reaction transfer completed successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
                
                return 1; // Return 1 indicating successful execution
            }
            catch (Exception ex)
            {
                // Catch any unexpected errors, log the stack trace to the user, and return -1
                MessageBox.Show(string.Format("An error occurred during Reaction Transfer:\n\n{0}\n{1}", ex.Message, ex.StackTrace), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return -1;
            }
        }
    }
}
