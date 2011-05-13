﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.Win32;

namespace IndentGuide
{
    [ComVisible(true)]
    public sealed class DisplayOptions : DialogPage
    {
        private IndentGuideService Service;
        private IVsTextManager TextManagerService;
        internal IVsEditorAdaptersFactoryService EditorAdapters;

        private IDictionary<string, IndentTheme> Themes;

        public DisplayOptions()
        {
            Upgrade();

            Themes = new Dictionary<string, IndentTheme>();
            Service = (IndentGuideService)ServiceProvider.GlobalProvider.GetService(typeof(SIndentGuide));
            TextManagerService = (IVsTextManager)ServiceProvider.GlobalProvider.GetService(typeof(SVsTextManager));
            
            var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
            EditorAdapters = (IVsEditorAdaptersFactoryService)componentModel
                .GetService<IVsEditorAdaptersFactoryService>();

            Service.Themes = Themes;
            Service.DefaultTheme = new IndentTheme(true);
        }

        private DisplayOptionsControl _Window = null;
        protected override System.Windows.Forms.IWin32Window Window
        {
            get
            {
                if (_Window == null)
                {
                    var newWindow = new DisplayOptionsControl(this);
                    System.Threading.Interlocked.CompareExchange(ref _Window, newWindow, null);
                }
                return _Window;
            }
        }

        internal RegistryKey RegistryRoot
        {
            get
            {
                var vsRoot = VSRegistry.RegistryRoot(Microsoft.VisualStudio.Shell.Interop.__VsLocalRegistryType.RegType_UserSettings);
                return vsRoot.OpenSubKey("IndentGuide");
            }
        }

        internal RegistryKey RegistryRootWritable
        {
            get
            {
                var vsRoot = VSRegistry.RegistryRoot(Microsoft.VisualStudio.Shell.Interop.__VsLocalRegistryType.RegType_UserSettings, true);
                return vsRoot.OpenSubKey("IndentGuide", true);
            }
        }

        public override void LoadSettingsFromStorage()
        {
            Themes.Clear();
            try
            {
                var reg = RegistryRoot;
                foreach (var themeName in reg.GetSubKeyNames())
                {
                    var theme = IndentTheme.Load(reg, themeName);
                    if (theme.IsDefault) Service.DefaultTheme = theme;
                    Themes[theme.Name] = theme;
                }
            }
            catch(Exception ex)
            {
                Trace.WriteLine(string.Format("LoadSettingsFromStorage: {0}", ex), "IndentGuide");
            }
            Service.OnThemesChanged();
        }

        public override void LoadSettingsFromXml(Microsoft.VisualStudio.Shell.Interop.IVsSettingsReader reader)
        {
            string xml;
            reader.ReadSettingXmlAsString("IndentGuide", out xml);
            var root = XElement.Parse(xml);
            Themes.Clear();
            foreach (var theme in root.Elements("Theme").Select(x => IndentTheme.Load(x)))
            {
                Themes[theme.Name] = theme;
            }
            Service.OnThemesChanged();
        }

        public override void SaveSettingsToStorage()
        {
            try
            {
                using (var reg = RegistryRootWritable)
                {
                    foreach (var theme in Themes.Values)
                    {
                        theme.Save(reg);
                    }
                }
            }
            catch(Exception ex)
            {
                Trace.WriteLine(string.Format("SaveSettingsToStorage: {0}", ex), "IndentGuide");
            }
        }

        public override void SaveSettingsToXml(Microsoft.VisualStudio.Shell.Interop.IVsSettingsWriter writer)
        {
            var root = new XElement("IndentGuide",
                Themes.Values.Select(t => t.ToXElement()));
            string xml = root.CreateReader().ReadOuterXml();
            writer.WriteSettingXmlFromString(xml);
        }

        protected override void OnActivate(CancelEventArgs e)
        {
            base.OnActivate(e);
            var doc = (DisplayOptionsControl)Window;
            doc.LocalThemes = Service.Themes.Values.OrderBy(t => t).Select(t => t.Clone()).ToList();

            try
            {
                IVsTextView view = null;
                IWpfTextView wpfView = null;
                TextManagerService.GetActiveView(0, null, out view);
                wpfView = EditorAdapters.GetWpfTextView(view);
                doc.CurrentContentType = wpfView.TextDataModel.ContentType.DisplayName;
            }
            catch
            {
                doc.CurrentContentType = null;
            }
        }

        protected override void OnApply(DialogPage.PageApplyEventArgs e)
        {
            var doc = (DisplayOptionsControl)Window;
            doc.SaveIfRequired();
            
            var changedThemes = doc.ChangedThemes;
            var deletedThemes = doc.DeletedThemes;
            if (changedThemes.Any())
            {
                foreach (var theme in changedThemes)
                {
                    Service.Themes[theme.Name] = theme;
                    if (theme.IsDefault) Service.DefaultTheme = theme;
                }
                if (!deletedThemes.Any()) Service.OnThemesChanged();
                changedThemes.Clear();
            }
            if (deletedThemes.Any())
            {
                var reg = RegistryRootWritable;
                foreach (var theme in deletedThemes)
                {
                    try { theme.Delete(reg); }
                    catch { }
                    Service.Themes.Remove(theme.Name);
                }
                Service.OnThemesChanged();
                deletedThemes.Clear();
            }

            base.OnApply(e);
        }

        public override void ResetSettings()
        {
            var reg = RegistryRootWritable;
            foreach (var theme in Service.Themes.Values)
            {
                try { theme.Delete(reg); }
                catch { }
            }
            Service.Themes.Clear();
            var defaultTheme = new IndentTheme(true);
            Service.Themes[defaultTheme.Name] = defaultTheme;
            defaultTheme.Save(reg);
            Service.OnThemesChanged();
        }

        #region Upgrade settings from v8.2
        
        private void Upgrade()
        {
            try
            {
                if (RegistryRoot != null) return;

                var vsRoot = VSRegistry.RegistryRoot(Microsoft.VisualStudio.Shell.Interop.__VsLocalRegistryType.RegType_UserSettings, true);

                using (var newKey = vsRoot.CreateSubKey("IndentGuide"))
                using (var key = vsRoot.OpenSubKey(SettingsRegistryPath))
                {
                    var theme = new IndentTheme(true);
                    if (key != null)
                    {
                        theme.Name = (string)key.GetValue("Name", IndentTheme.DefaultThemeName);
                        theme.EmptyLineMode = (EmptyLineMode)TypeDescriptor.GetConverter(typeof(EmptyLineMode))
                            .ConvertFromInvariantString((string)key.GetValue("EmptyLineMode"));

                        theme.LineFormat.LineColor = (Color)TypeDescriptor.GetConverter(typeof(Color))
                            .ConvertFromInvariantString((string)key.GetValue("LineColor"));
                        theme.LineFormat.LineStyle = (LineStyle)TypeDescriptor.GetConverter(typeof(LineStyle))
                            .ConvertFromInvariantString((string)key.GetValue("LineStyle"));
                        theme.LineFormat.Visible = bool.Parse((string)key.GetValue("Visible"));
                    }

                    theme.Save(newKey);
                }

                vsRoot.DeleteSubKeyTree(SettingsRegistryPath, false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(string.Format("Upgrade: {0}", ex), "IndentGuide");
            }
        }

        #endregion
    }
}
