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
                    // ShowDialog blocks execution until the user clicks OK or Cancel.
                    // The form validates the path and the tolerance itself, so by the time OK comes back
                    // both are known good.
                    if (form.ShowDialog() != DialogResult.OK) return 1;

                    // Modifying an existing model rewrites a file the user already owns, so it is confirmed
                    // once here rather than assumed. A timestamped backup is written either way.
                    if (form.ModifyExistingFile)
                    {
                        DialogResult go = MessageBox.Show(
                            "The reactions will be added to this existing model:\n\n" + form.txtFilePath.Text +
                            "\n\nA timestamped backup is written next to it first, and nothing in it is deleted." +
                            "\n\nAxisVM will switch to the target model when this finishes. Continue?",
                            "Modify Existing Model", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

                        if (go != DialogResult.OK) return 1;
                    }

                    // Collect user settings from the form into a strongly-typed settings object
                    ReactionTransferSettings settings = new ReactionTransferSettings
                    {
                        TargetFilePath = form.txtFilePath.Text,

                        ModifyExistingFile = form.ModifyExistingFile,

                        TransferSupports = form.chkTransferSupports.Checked,

                        ResultType = form.cbResultType.SelectedIndex, // 0 = Linear Static, 1 = NonLinear Static, 2 = Envelope

                        NodeMatchToleranceM = form.NodeMatchToleranceM,

                        EnvelopeComponent = form.SelectedEnvelopeComponent,
                        EnvelopeMinMax = form.SelectedEnvelopeMinMax
                    };

                    // Execute the core business logic. It reports its own outcome, including a
                    // per-stage breakdown, so there is no follow-up dialog here.
                    ReactionTransferLogic.ExecuteTransfer(axisApp, currentModel, settings);
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
