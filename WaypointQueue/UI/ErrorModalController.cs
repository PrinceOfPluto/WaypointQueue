using System;
using System.IO;
using TMPro;
using UI.Builder;
using UI.Common;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue.UI
{
    internal class ErrorModalController : MonoBehaviour
    {
        private static ErrorModalController _shared;
        public static ErrorModalController Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = FindObjectOfType<ErrorModalController>();
                }

                return _shared;
            }
        }

        protected void OnDestroy()
        {
            _shared = null;
        }

        public void ShowProcessingErrorModal(string errorMessage, string orderType, ManagedWaypoint wp)
        {
            Loader.LogDebug("Present processing error modal");
            ModalAlertController.Present((UIPanelBuilder builder, Action dismiss) =>
            {
                builder.Spacing = 16f;
                builder.AddLabel("Waypoint Error Detected", delegate (TMP_Text text)
                {
                    text.fontSize = 22f;
                    text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                });

                BuildTextBody(builder, $"Error detected while processing current waypoint for locomotive {wp.Locomotive.Ident}.");

                builder.HStack(builder =>
                {
                    builder.AddButtonMedium("Jump to waypoint", () =>
                    {
                        JumpCameraToWaypoint(wp);
                    });
                    builder.AddButtonMedium($"Select {wp.Locomotive.Ident}", () =>
                    {
                        TrainController.Shared.SelectedCar = wp.Locomotive;
                    });
                });

                BuildTextBody(builder, $"{orderType} orders could not be completed due to the following error:");

                builder.AddLabelMarkup($"- <indent=20f>{errorMessage}").HorizontalTextAlignment(HorizontalAlignmentOptions.Left);

                builder.Spacer(8f);

                BuildSubheaderLabel(builder, "Suggested Actions");

                builder.AddLabelMarkup($"""
                    - <indent=20f>Check if the configured waypoint orders are possible, such as whether a target car is available for coupling or uncoupling.
                    - <indent=20f>Verify you are using the latest version of Waypoint Queue.
                    - <indent=20f>Submit a bug report on GitHub and attach your Player.log file.
                    - <indent=20f>Take a screenshot of the waypoint window, the game area around the waypoint, and any involved cars to include in a bug report.
                    """).HorizontalTextAlignment(HorizontalAlignmentOptions.Left);

                builder.Spacer(8f);

                BuildSubheaderLabel(builder, "Queue Paused");
                builder.AddLabelMarkup($"""
                    - <indent=20f>The waypoint queue for {wp.Locomotive.Ident} will remain <b>PAUSED</b> until this waypoint is deleted.
                    - <indent=20f>You may view this error message again in the waypoints window for {wp.Locomotive.Ident}.
                    """).HorizontalTextAlignment(HorizontalAlignmentOptions.Left);

                builder.AddButtonMedium("Delete waypoint", () =>
                {
                    WaypointQueueController.Shared.RemoveWaypoint(wp);
                    dismiss?.Invoke();
                });

                builder.Spacer(16f);

                BuildErrorModalButtons(builder, dismiss);
            }, width: 800);
        }

        public void ShowTickErrorModal(string errorMessage)
        {
            Loader.LogDebug("Present tick error modal");
            ModalAlertController.Present((UIPanelBuilder builder, Action dismiss) =>
            {
                builder.Spacing = 16f;
                builder.AddLabel("Waypoint Error Detected", delegate (TMP_Text text)
                {
                    text.fontSize = 22f;
                    text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                });

                BuildTextBody(builder, $"""
                    Waypoint Queue encountered an error while processing mod tick updates.
                    
                    The mod cannot continue processing waypoints due to the following error:
                    """);

                builder.AddLabelMarkup($"- <indent=20f>{errorMessage}").HorizontalTextAlignment(HorizontalAlignmentOptions.Left);

                builder.Spacer(8f);

                BuildSubheaderLabel(builder, "Suggested Actions");
                builder.AddLabelMarkup($"""
                - <indent=20f>Verify you are using the latest version of Waypoint Queue.
                - <indent=20f>Check if reloading the game resolves the issue.
                - <indent=20f>Submit a bug report on GitHub and attach your Player.log file.
                - <indent=20f>Include a brief description of what was happening in-game before the error occured.
                """).HorizontalTextAlignment(HorizontalAlignmentOptions.Left);

                builder.Spacer(16f);

                BuildErrorModalButtons(builder, dismiss);

            }, width: 800);
        }

        private void BuildSubheaderLabel(UIPanelBuilder builder, string label)
        {
            builder.AddLabel(label, (TMP_Text text) =>
            {
                text.fontSize = 20f;
                text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                text.fontWeight = FontWeight.Bold;
            });
        }

        private void BuildTextBody(UIPanelBuilder builder, string body)
        {
            builder.AddLabel(body, (TMP_Text text) =>
            {
                text.fontSize = 18f;
                text.horizontalAlignment = HorizontalAlignmentOptions.Left;
            });
        }

        private void BuildErrorModalButtons(UIPanelBuilder builder, Action dismiss)
        {
            builder.AlertButtons(delegate (UIPanelBuilder builder)
            {
                builder.AddButtonMedium("Report bug on GitHub", () =>
                {
                    OpenBugReportOnGitHub();
                });
                builder.AddButtonMedium("Open Player.log", () =>
                {
                    OpenPlayerLogFile();
                });
                builder.AddButtonMedium("Close", () =>
                {
                    dismiss?.Invoke();
                });
            });
        }

        private void JumpCameraToWaypoint(ManagedWaypoint waypoint)
        {
            CameraSelector.shared.JumpToPoint(waypoint.Location.GetPosition(), waypoint.Location.GetRotation(), CameraSelector.CameraIdentifier.Strategy);
        }

        private void OpenBugReportOnGitHub()
        {
            string bugReportIssueLink = "https://github.com/PrinceOfPluto/WaypointQueue/issues";
            Application.OpenURL(bugReportIssueLink);
        }

        private void OpenPlayerLogFile()
        {
            string filePath = Path.Combine(Application.persistentDataPath, "Player.log");
            if (File.Exists(filePath))
            {
                Application.OpenURL(filePath);
            }
        }
    }
}
