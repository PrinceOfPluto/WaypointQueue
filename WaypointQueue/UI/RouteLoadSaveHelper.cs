using Network;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UI.Builder;
using UI.Common;
using UnityEngine;
using WaypointQueue.State;
using WaypointQueue.UUM;
using static WaypointQueue.ModSaveManager;

namespace WaypointQueue.UI
{
    internal class RouteLoadSaveHelper
    {
        private enum Mode
        {
            Load,
            Save
        }

        private readonly string _path;

        private string _filename;

        private string FilePath
        {
            get
            {
                if (_filename != null)
                {
                    return Path.Combine(_path, _filename);
                }

                return _path;
            }
        }

        public RouteLoadSaveHelper()
        {
            _path = Path.Combine(Application.persistentDataPath, "Routes");
        }

        private void LoadSave(Mode mode, List<RouteDefinition> routesToSave)
        {
            if (mode == Mode.Load)
            {
                _filename = null;
            }

            ModalAlertController.Present(delegate (UIPanelBuilder builder, Action dismissAction)
            {
                UIPanelBuilder alertBuilder = builder;
                builder.Spacing = 16f;
                string title;
                string subtitle;
                switch (mode)
                {
                    case Mode.Load:
                        title = "Load from Disk";
                        subtitle = "Select a file to load routes from.";
                        break;
                    case Mode.Save:
                        title = "Save to Disk";
                        subtitle = "Select a file to overwrite or enter a new filename.";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("mode", mode, null);
                }

                builder.AddLabel("<b>" + title + "</b>\n" + subtitle);
                string normalizedRoutesPath = NormalizePath(_path);
                builder.AddLabel("<size=12><#8D8A81>Routes Directory: " + normalizedRoutesPath);
                builder.VScrollView(delegate (UIPanelBuilder builder)
                {
                    foreach (string filename2 in Filenames())
                    {
                        builder.HStack(delegate (UIPanelBuilder builder)
                        {
                            builder.AddLabel(filename2).FlexibleWidth();
                            builder.AddButtonCompact("Select", delegate
                            {
                                _filename = filename2;
                                alertBuilder.Rebuild();
                            }).Width(80f);
                        });
                    }
                }).Height(250f);
                builder.AddField("Filename", mode switch
                {
                    Mode.Save => builder.AddInputField(_filename, delegate (string filename)
                    {
                        _filename = filename;
                    }, "routes.json"),
                    Mode.Load => builder.AddLabel(string.IsNullOrEmpty(_filename) ? "<i>Select a file</i>" : _filename),
                    _ => throw new ArgumentOutOfRangeException("mode", mode, null),
                });
                builder.AlertButtons(delegate (UIPanelBuilder builder)
                {
                    switch (mode)
                    {
                        case Mode.Load:
                            builder.AddButton("Load", delegate
                            {
                                Load();
                                dismissAction();
                            }).Disable(!File.Exists(FilePath));
                            break;
                        case Mode.Save:
                            builder.AddButton("Save", delegate
                            {
                                Save(routesToSave);
                                dismissAction();
                            });
                            break;
                        default:
                            throw new ArgumentOutOfRangeException("mode", mode, null);
                    }

                    builder.AddButton("Cancel", dismissAction);
                });
            });
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            path = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return path;
        }

        public void PromptToSaveRoutes()
        {
            LoadSave(Mode.Save, [.. ModStateManager.Shared.Routes.Values]);
        }

        public void PromptToLoadRoutes()
        {
            if (Multiplayer.IsHost)
            {
                LoadSave(Mode.Load, null);
            }
            else
            {
                Toast.Present("Only host may load routes from disk");
            }
        }

        private IEnumerable<string> Filenames()
        {
            if (!Directory.Exists(_path))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(_path, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName);
        }

        private void Save(List<RouteDefinition> routesToSave)
        {
            try
            {
                if (!Directory.Exists(_path))
                {
                    Directory.CreateDirectory(_path);
                }

                RouteDefinitionSaveState saveState = new RouteDefinitionSaveState
                {
                    Version = 2,
                    RouteDefinitions = routesToSave
                };

                string contents = JsonConvert.SerializeObject(saveState, Formatting.Indented);
                File.WriteAllText(FilePath, contents);
                Toast.Present("Saved.");
            }
            catch (Exception ex)
            {
                ModalAlertController.PresentOkay("Error saving " + _filename, ex.Message);
            }
        }

        private void Load()
        {
            try
            {
                string path = Path.Combine(_path, _filename);
                if (!File.Exists(path))
                {
                    Toast.Present("File not found.");
                    return;
                }

                string json = File.ReadAllText(path);
                RouteDefinitionSaveState data = JsonConvert.DeserializeObject<RouteDefinitionSaveState>(json);
                Loader.Log($"Loaded {data.RouteDefinitions?.Count ?? 0} routes from '{_filename}'.");

                ModStateManager.Shared.RegisterRoutesFromLoad(data.RouteDefinitions);
            }
            catch (Exception ex)
            {
                ModalAlertController.PresentOkay("Error loading routes:", ex.Message);
            }
        }
    }
}
