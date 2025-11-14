using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    
    
    
    
    
    public static class RouteSaveManager
    {
        private static readonly string RoutesPath =
            Path.Combine(Application.persistentDataPath, "Routes");

        public static string GetRoutesDirectory()
        {
            if (!Directory.Exists(RoutesPath))
                Directory.CreateDirectory(RoutesPath);

            var saveName = WaypointSaveManager.RoutesSaveDir;
            if (string.IsNullOrWhiteSpace(saveName))
                saveName = "Global";

            var dir = Path.Combine(RoutesPath, saveName);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return dir;
        }

        public static List<RouteDefinition> LoadAll()
        {
            var list = new List<RouteDefinition>();
            var dir = GetRoutesDirectory();
            foreach (var file in Directory.GetFiles(dir, "*.route.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var route = JsonConvert.DeserializeObject<RouteDefinition>(json);
                    if (route != null)
                    {
                        route.FilePath = file;
                        list.Add(route);
                    }
                }
                catch (Exception e)
                {
                    Loader.Log($"[Routes] Failed to load {file}: {e}");
                }
            }
            return list;
        }

        public static void Save(RouteDefinition route)
        {
            if (route == null) return;
            GetRoutesDirectory();

            if (string.IsNullOrEmpty(route.FilePath))
                route.FilePath = PathFor(route);

            var json = JsonConvert.SerializeObject(route, Formatting.Indented);
            File.WriteAllText(route.FilePath, json);
            Loader.Log($"[Routes] Saved '{route.Name}' → {route.FilePath}");
        }

        public static void SaveAs(RouteDefinition route, string newName)
        {
            if (route == null) return;
            route.Name = string.IsNullOrWhiteSpace(newName) ? route.Name : newName.Trim();

            var old = route.FilePath;
            route.FilePath = PathFor(route);
            Save(route);

            if (!string.IsNullOrEmpty(old) && File.Exists(old) && old != route.FilePath)
            {
                try { File.Delete(old); } catch { /* ignore */ }
            }
        }

        public static void Delete(RouteDefinition route)
        {
            if (route == null) return;
            try
            {
                if (!string.IsNullOrEmpty(route.FilePath) && File.Exists(route.FilePath))
                    File.Delete(route.FilePath);
            }
            catch (Exception e)
            {
                Loader.Log($"[Routes] Failed to delete {route.FilePath}: {e}");
            }
        }

        public static void Rename(RouteDefinition route, string newName, bool moveFile = true)
        {
            if (route == null) return;
            newName = (newName ?? "").Trim();
            if (newName.Length == 0 || newName == route.Name) return;

            route.Name = newName;

            if (!moveFile || string.IsNullOrEmpty(route.FilePath) || !File.Exists(route.FilePath))
            {
                Save(route);
                return;
            }

            var newPath = PathFor(route);
            if (newPath == route.FilePath)
            {
                Save(route);
                return;
            }

            var json = JsonConvert.SerializeObject(route, Formatting.Indented);
            File.WriteAllText(newPath, json);
            try { File.Delete(route.FilePath); } catch { /* ignore */ }
            route.FilePath = newPath;
            Loader.Log($"[Routes] Renamed to '{route.Name}' → {route.FilePath}");
        }

        
        private static string PathFor(RouteDefinition route)
        {
            var dir = GetRoutesDirectory();
            var name = SanitizeFileName(route?.Name ?? "Route");
            var id = string.IsNullOrEmpty(route?.Id) ? "noid" : route.Id;
            return Path.Combine(dir, $"{name}_{id}.route.json");
        }

        private static string SanitizeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            if (s.Length > 60) s = s.Substring(0, 60);
            return string.IsNullOrWhiteSpace(s) ? "Route" : s.Trim();
        }
    }
}
