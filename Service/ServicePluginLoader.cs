using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using SwiftList.Core;
using SwiftList.PluginSdk;
using Logger = SwiftList.Core.Logger;

namespace SwiftList.Service
{
    public static class ServicePluginLoader
    {
        public static void LoadPlugins()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string pluginsDir = Path.Combine(baseDir, "Plugins");

                Logger.Log($"[ServicePluginLoader] Scanning for alias plugins in: {pluginsDir}");

                if (!Directory.Exists(pluginsDir))
                {
                    Directory.CreateDirectory(pluginsDir);
                    return;
                }

                var translationProviders = new List<ITranslationProvider>();
                var aliasProviders = new List<IAliasProvider>();

                string[] dllFiles = Directory.GetFiles(pluginsDir, "*.dll");
                foreach (string dllFile in dllFiles)
                {
                    try
                    {
                        Assembly assembly = Assembly.LoadFrom(dllFile);
                        foreach (Type type in assembly.GetTypes())
                        {
                            if (type.IsInterface || type.IsAbstract)
                                continue;

                            if (typeof(IAliasProvider).IsAssignableFrom(type))
                            {
                                IAliasProvider provider = (IAliasProvider)Activator.CreateInstance(type)!;
                                aliasProviders.Add(provider);
                            }

                            if (typeof(ITranslationProvider).IsAssignableFrom(type))
                            {
                                ITranslationProvider provider = (ITranslationProvider)Activator.CreateInstance(type)!;
                                translationProviders.Add(provider);
                                Logger.Log($"[ServicePluginLoader] Loaded translation provider: '{type.Name}' from {Path.GetFileName(dllFile)}");
                            }

                            if (typeof(IActivePathCollector).IsAssignableFrom(type))
                            {
                                IActivePathCollector provider = (IActivePathCollector)Activator.CreateInstance(type)!;
                                ActivePathCollectorRegistry.Register(provider);
                                Logger.Log($"[ServicePluginLoader] Loaded active path collector: '{type.Name}' from {Path.GetFileName(dllFile)}");
                            }

                            if (typeof(IFileDialogAdapter).IsAssignableFrom(type))
                            {
                                IFileDialogAdapter provider = (IFileDialogAdapter)Activator.CreateInstance(type)!;
                                FileDialogAdapterRegistry.Register(provider);
                                Logger.Log($"[ServicePluginLoader] Loaded file dialog adapter: '{type.Name}' from {Path.GetFileName(dllFile)}");
                            }

                            if (typeof(IInlineSearchAdapter).IsAssignableFrom(type))
                            {
                                IInlineSearchAdapter provider = (IInlineSearchAdapter)Activator.CreateInstance(type)!;
                                InlineSearchAdapterRegistry.Register(provider);
                                Logger.Log($"[ServicePluginLoader] Loaded inline search adapter: '{type.Name}' from {Path.GetFileName(dllFile)}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ServicePluginLoader] Failed to load plugin assembly {Path.GetFileName(dllFile)}: {ex.Message}", SwiftList.Core.LogLevel.Error);
                    }
                }

                // Initialize TranslationService LookupFunc in the service process using the loaded translation providers
                var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string cultureName = System.Globalization.CultureInfo.CurrentUICulture.Name;
                foreach (var provider in translationProviders)
                {
                    try
                    {
                        var dict = provider.GetTranslations(cultureName);
                        if (dict != null)
                        {
                            foreach (var kvp in dict)
                            {
                                translations[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ServicePluginLoader] Failed to load translations from '{provider.Name}': {ex.Message}", SwiftList.Core.LogLevel.Error);
                    }
                }

                TranslationService.LookupFunc = key => translations.TryGetValue(key, out var val) ? val : $"[{key}]";

                // Wire up FilterFuncs so the hook process respects enabled/disabled state.
                // The lambda reads UserSettings.Load() (cached) on every call, so after a
                // ReloadSettings command triggers UserSettings.ForceReload() the next adapter
                // lookup will automatically reflect the new disabled-components list.
                static bool IsAdapterEnabled(object obj)
                {
                    try
                    {
                        string dllName = Path.GetFileName(obj.GetType().Assembly.Location);
                        string typeName = obj.GetType().Name;
                        var settings = UserSettings.Load();
                        // Match the same ID format used by App's ComponentFilter
                        string idInlineSearch = $"{dllName}::InlineSearchAdapter::{typeName}";
                        string idFileDialog = $"{dllName}::FileDialogAdapter::{typeName}";
                        string idPathCollect = $"{dllName}::ActivePathCollector::{typeName}";
                        return !settings.DisabledPluginComponents.Contains(idInlineSearch, StringComparer.OrdinalIgnoreCase)
                            && !settings.DisabledPluginComponents.Contains(idFileDialog, StringComparer.OrdinalIgnoreCase)
                            && !settings.DisabledPluginComponents.Contains(idPathCollect, StringComparer.OrdinalIgnoreCase);
                    }
                    catch { return true; }
                }

                InlineSearchAdapterRegistry.FilterFunc = a => IsAdapterEnabled(a);
                FileDialogAdapterRegistry.FilterFunc = a => IsAdapterEnabled(a);
                ActivePathCollectorRegistry.FilterFunc = a => IsAdapterEnabled(a);

                // Now register alias providers (this will trigger provider.Name evaluation)
                foreach (var provider in aliasProviders)
                {
                    AliasProviderRegistry.Register(provider);
                    Logger.Log($"[ServicePluginLoader] Loaded alias provider: '{provider.GetType().Name}'");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ServicePluginLoader] Error while loading plugins: {ex.Message}", SwiftList.Core.LogLevel.Error);
            }
        }
    }
}
