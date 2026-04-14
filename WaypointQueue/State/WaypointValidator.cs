using Model;
using System;
using System.Collections.Generic;
using TMPro;
using UI.Builder;
using UI.Common;
using WaypointQueue.UUM;

namespace WaypointQueue.State
{
    internal static class WaypointValidator
    {
        internal struct ValidationMessage(string scope, string message, ValidationType type)
        {
            public string Scope { get; set; } = scope;
            public string Message { get; set; } = message;
            public ValidationType Type { get; set; } = type;

            public bool IsError()
            {
                return Type == ValidationType.LocomotiveNotFound || Type == ValidationType.TrackLocationNotFound || Type == ValidationType.CoupleToCarIdNotFound;
            }
        }

        public enum ValidationType
        {
            LocomotiveNotFound,
            TrackLocationNotFound,
            CoupleToCarIdNotFound,
            UncoupleByDestinationIdNotFound
        }

        public static List<ValidationMessage> ValidateWaypoint(ManagedWaypoint waypoint, string scope)
        {
            List<ValidationMessage> errors = [];

            if (!waypoint.TryResolveLocation(out Track.Location loc))
            {
                errors.Add(new ValidationMessage(scope, $"Failed to find track location {waypoint.LocationString} for waypoint {waypoint.Id}", ValidationType.TrackLocationNotFound));
            }

            if (!String.IsNullOrEmpty(waypoint.CoupleToCarId) && !waypoint.TryResolveCoupleToCar(out Car coupleToCar))
            {
                errors.Add(new ValidationMessage(scope, $"Failed to find couple to car id {waypoint.CoupleToCarId} for waypoint {waypoint.Id}", ValidationType.CoupleToCarIdNotFound));
            }

            if (waypoint.WillUncoupleByDestination && !waypoint.CheckValidUncoupleDestinationId())
            {
                errors.Add(new ValidationMessage(scope, $"Failed to find uncoupling by destination id {waypoint.UncoupleDestinationId} for waypoint {waypoint.Id}", ValidationType.UncoupleByDestinationIdNotFound));
            }

            errors.ForEach(v => LogValidationMessage(v));

            return errors;
        }

        public static void LogValidationMessage(ValidationMessage message)
        {
            Loader.LogError($"Validation error for {message.Scope}: {message.Message}");
        }

        public static void PresentValidationMessageModal(List<ValidationMessage> queueMessages, List<ValidationMessage> routeMessages)
        {
            Loader.LogError($"Presenting validation error modal with the following errors");
            queueMessages.ForEach(v => LogValidationMessage(v));
            routeMessages.ForEach(v => LogValidationMessage(v));

            ModalAlertController.Present((UIPanelBuilder builder, Action dismiss) =>
            {
                builder.Spacing = 16f;
                builder.AddLabel("Invalid waypoint data detected", delegate (TMP_Text text)
                {
                    text.fontSize = 22f;
                    text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                });

                builder.AddLabel($"""
                    An issue was detected when trying to load previously saved waypoint data.

                    Sometimes this may happen if any rolling stock or map/track mods were modified or removed in this save.
                    """, (TMP_Text text) =>
                {
                    text.fontSize = 18f;
                    text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                });

                builder.VScrollView(scroll =>
                {
                    builder.VStack(stack =>
                    {
                        foreach (var message in queueMessages)
                        {
                            stack.AddLabelMarkup($"- <indent=20f>{message.Scope} - {message.Message}");
                            stack.Spacer(20f);
                        }
                    });
                });

                builder.AddLabel($"""
                    Waypoint Queue should still work normally with this save game, though some waypoints may be missing.
                    """, (TMP_Text text) =>
                {
                    text.fontSize = 18f;
                    text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                });

                builder.AlertButtons(alertButtons =>
                {
                    alertButtons.AddButtonMedium("Okay", () =>
                    {
                        dismiss?.Invoke();
                    });
                });
            }, width: 800);
        }
    }
}
