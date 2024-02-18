using System;
using System.Collections.Generic;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.AddressableAssets.Util;
using UnityEngine.Serialization;

// ReSharper disable DelegateSubtraction

namespace UnityEditor.AddressableAssets.GUI
{
    [Serializable]
    internal class AddressableAssetsSettingsGroupEditor
    {
        [System.AttributeUsage(AttributeTargets.Class)]
        public class HideBuildMenuInUI : Attribute
        {
        }

        internal struct BuildMenuContext
        {
            public byte buildScriptIndex;
            public AddressableAssetSettings Settings { get; set; }
        }

        [FormerlySerializedAs("treeState")]
        [SerializeField]
        TreeViewState m_TreeState;

        [FormerlySerializedAs("mchs")]
        [SerializeField]
        MultiColumnHeaderState m_Mchs;

        internal AddressableAssetEntryTreeView m_EntryTree;

        public AddressableAssetsWindow window;

        SearchField m_SearchField;
        const int k_SearchHeight = 20;

        AddressableAssetSettings m_Settings;

        internal AddressableAssetSettings settings
        {
            get
            {
                if (m_Settings == null) m_Settings = AddressableAssetSettingsDefaultObject.Settings;
                return m_Settings;
            }
            set => m_Settings = value;
        }

        bool m_ResizingVerticalSplitter;
        Rect m_VerticalSplitterRect = new Rect(0, 0, 10, k_SplitterWidth);

        [SerializeField]
        float m_VerticalSplitterPercent;

        const int k_SplitterWidth = 3;


        public AddressableAssetsSettingsGroupEditor(AddressableAssetsWindow w)
        {
            window = w;
            m_VerticalSplitterPercent = 0.8f;
            OnEnable();
        }

        public void SelectEntries(IList<AddressableAssetEntry> entries)
        {
            List<int> selectedIDs = new List<int>(entries.Count);
            Stack<AssetEntryTreeViewItem> items = new Stack<AssetEntryTreeViewItem>();

            if (m_EntryTree == null || m_EntryTree.Root == null)
                InitialiseEntryTree();

            foreach (TreeViewItem item in m_EntryTree.Root.children)
            {
                if (item is AssetEntryTreeViewItem i)
                    items.Push(i);
            }

            while (items.Count > 0)
            {
                var i = items.Pop();

                bool contains = false;
                if (i.entry != null)
                {
                    foreach (AddressableAssetEntry entry in entries)
                    {
                        // class instances can be different but refer to the same entry, use guid
                        if (entry.guid == i.entry.guid && i.entry.MainAsset == entry.MainAsset)
                        {
                            contains = true;
                            break;
                        }
                    }
                }

                if (!i.IsGroup && contains)
                {
                    selectedIDs.Add(i.id);
                }
                else if (i.hasChildren)
                {
                    foreach (TreeViewItem child in i.children)
                    {
                        if (child is AssetEntryTreeViewItem c)
                            items.Push(c);
                    }
                }
            }

            foreach (int i in selectedIDs)
                m_EntryTree.FrameItem(i);
            m_EntryTree.SetSelection(selectedIDs);
        }

        public void SelectGroup(AddressableAssetGroup group, bool fireSelectionChanged)
        {
            var items = new Stack<AssetEntryTreeViewItem>();

            if (m_EntryTree == null || m_EntryTree.Root == null)
                InitialiseEntryTree();

            foreach (TreeViewItem item in m_EntryTree.Root.children)
            {
                if (item is AssetEntryTreeViewItem i)
                    items.Push(i);
            }

            while (items.Count > 0)
            {
                AssetEntryTreeViewItem item = items.Pop();

                if (item.IsGroup && item.group.Guid == group.Guid)
                {
                    m_EntryTree.FrameItem(item.id);
                    var selectedIds = new List<int>() {item.id};
                    if (fireSelectionChanged)
                        m_EntryTree.SetSelection(selectedIds, TreeViewSelectionOptions.FireSelectionChanged);
                    else
                        m_EntryTree.SetSelection(selectedIds);
                    return;
                }
                else if (!string.IsNullOrEmpty(item.folderPath) && item.hasChildren)
                {
                    foreach (TreeViewItem child in item.children)
                    {
                        if (child is AssetEntryTreeViewItem c)
                            items.Push(c);
                    }
                }
            }
        }

        void OnSettingsModification(AddressableAssetSettings s, AddressableAssetSettings.ModificationEvent e, object o)
        {
            if (m_EntryTree == null)
                return;

            switch (e)
            {
                case AddressableAssetSettings.ModificationEvent.GroupAdded:
                case AddressableAssetSettings.ModificationEvent.GroupRemoved:
                case AddressableAssetSettings.ModificationEvent.EntryAdded:
                case AddressableAssetSettings.ModificationEvent.EntryMoved:
                case AddressableAssetSettings.ModificationEvent.EntryRemoved:
                case AddressableAssetSettings.ModificationEvent.GroupRenamed:
                case AddressableAssetSettings.ModificationEvent.EntryModified:
                case AddressableAssetSettings.ModificationEvent.BatchModification:
                    m_EntryTree.Reload();
                    if (window != null)
                        window.Repaint();
                    break;
            }
        }

        GUIStyle GetStyle(string styleName)
        {
            GUIStyle s = UnityEngine.GUI.skin.FindStyle(styleName);
            if (s == null)
                s = EditorGUIUtility.GetBuiltinSkin(EditorSkin.Inspector).FindStyle(styleName);
            if (s == null)
            {
                L.E("Missing built-in guistyle " + styleName);
                s = new GUIStyle();
            }

            return s;
        }

        [NonSerialized]
        List<GUIStyle> m_SearchStyles;

        [NonSerialized]
        GUIStyle m_ButtonStyle;

        [NonSerialized]
        Texture2D m_CogIcon;

        void TopToolbar(Rect toolbarPos)
        {
            if (m_SearchStyles == null)
            {
                m_SearchStyles = new List<GUIStyle>();
                m_SearchStyles.Add(GetStyle("ToolbarSearchTextFieldPopup")); //GetStyle("ToolbarSeachTextField");
                m_SearchStyles.Add(GetStyle("ToolbarSearchCancelButton"));
                m_SearchStyles.Add(GetStyle("ToolbarSearchCancelButtonEmpty"));
            }

            if (m_ButtonStyle == null)
                m_ButtonStyle = GetStyle("ToolbarButton");
            if (m_CogIcon == null)
                m_CogIcon = EditorGUIUtility.FindTexture("_Popup");


            GUILayout.BeginArea(new Rect(0, 0, toolbarPos.width, k_SearchHeight));

            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                float spaceBetween = 4f;


                {
                    var guiMode = new GUIContent("New", "Create a new group");
                    Rect rMode = GUILayoutUtility.GetRect(guiMode, EditorStyles.toolbarDropDown);
                    if (UnityEngine.GUI.Button(rMode, guiMode))
                        m_EntryTree.CreateNewGroup();
                }

                {
                    var guiMode = new GUIContent("Tools", "Tools used to configure or analyze Addressable Assets");
                    Rect rMode = GUILayoutUtility.GetRect(guiMode, EditorStyles.toolbarDropDown);
                    if (EditorGUI.DropdownButton(rMode, guiMode, FocusType.Passive, EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Inspect System Settings"), false, () =>
                        {
                            EditorApplication.ExecuteMenuItem("Window/General/Inspector");
                            EditorGUIUtility.PingObject(AddressableAssetSettingsDefaultObject.Settings);
                            Selection.activeObject = AddressableAssetSettingsDefaultObject.Settings;
                        });

                        menu.AddItem(new GUIContent("Analyze"), false, AnalyzeWindow.ShowWindow);

                        menu.AddItem(new GUIContent("Addressables Report"), false, BuildReportVisualizer.BuildReportWindow.ShowWindow);

                        menu.DropDown(rMode);
                    }
                }

                GUILayout.FlexibleSpace();
                if (toolbarPos.width > 300)
                    GUILayout.Space(spaceBetween * 2f + 8);

                {
                    string playmodeButtonName = toolbarPos.width < 300 ? "Play Mode" : "Play Mode Script";
                    var guiMode = new GUIContent(playmodeButtonName, "Determines how the Addressables system loads assets in Play Mode");
                    Rect rMode = GUILayoutUtility.GetRect(guiMode, EditorStyles.toolbarDropDown);
                    if (EditorGUI.DropdownButton(rMode, guiMode, FocusType.Passive, EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        for (byte i = 0; i < settings.DataBuilders.Count; i++)
                        {
                            var m = settings.GetDataBuilder(i);
                            if (m.CanBuildData<AddressablesPlayModeBuildResult>())
                            {
                                string text = m is Build.DataBuilders.BuildScriptPackedPlayMode
                                    ? $"{m.Name} ({EditorUserBuildSettings.activeBuildTarget})"
                                    : m.Name;
                                menu.AddItem(new GUIContent(text), i == settings.ActivePlayModeDataBuilderIndex, OnSetActivePlayModeScript, i);
                            }
                        }

                        menu.DropDown(rMode);
                    }
                }

                var guiBuild = new GUIContent("Build", "Options for building Addressable Assets");
                Rect rBuild = GUILayoutUtility.GetRect(guiBuild, EditorStyles.toolbarDropDown);
                if (EditorGUI.DropdownButton(rBuild, guiBuild, FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    var genericDropdownMenu = new GenericMenu();
                    var addressablesPlayerBuildResultBuilderExists = false;
                    for (byte i = 0; i < settings.DataBuilders.Count; i++)
                    {
                        var dataBuilder = settings.GetDataBuilder(i);
                        if (dataBuilder.CanBuildData<AddressablesPlayerBuildResult>() is false)
                            continue;

                        addressablesPlayerBuildResultBuilderExists = true;
                        BuildMenuContext context = new BuildMenuContext()
                        {
                            buildScriptIndex = i,
                            Settings = settings
                        };

                        genericDropdownMenu.AddItem(new GUIContent(dataBuilder.Name), false, OnBuildAddressables, context);
                    }

                    if (!addressablesPlayerBuildResultBuilderExists)
                        genericDropdownMenu.AddDisabledItem(new GUIContent("/No Build Script Available"));

                    genericDropdownMenu.AddSeparator("");
                    genericDropdownMenu.AddItem(new GUIContent("Clear Build Cache/All"), false, OnCleanAll);
                    genericDropdownMenu.AddItem(new GUIContent("Clear Build Cache/Content Builders/All"), false, OnCleanAddressables, null);
                    for (byte i = 0; i < settings.DataBuilders.Count; i++)
                    {
                        var m = settings.GetDataBuilder(i);
                        genericDropdownMenu.AddItem(new GUIContent("Clear Build Cache/Content Builders/" + m.Name), false, OnCleanAddressables, m);
                    }

                    genericDropdownMenu.AddItem(new GUIContent("Clear Build Cache/Build Pipeline Cache"), false, OnCleanSBP);
                    genericDropdownMenu.DropDown(rBuild);
                }

                GUILayout.Space(4);
                var searchRect = GUILayoutUtility.GetRect(0, toolbarPos.width * 0.6f, 16f, 16f, m_SearchStyles[0], GUILayout.MinWidth(65), GUILayout.MaxWidth(300));
                var baseSearch = m_EntryTree.searchString;
                var searchString = m_SearchField.OnGUI(searchRect, baseSearch, m_SearchStyles[0], m_SearchStyles[1], m_SearchStyles[2]);
                if (baseSearch != searchString)
                    m_EntryTree?.Search(searchString);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static void OnBuildAddressables(object ctx)
        {
            BuildMenuContext buildAddressablesContext = (BuildMenuContext) ctx;
            OnBuildAddressables(buildAddressablesContext);
        }

        internal static void OnBuildAddressables(BuildMenuContext context)
        {
            context.Settings.ActivePlayerDataBuilderIndex = context.buildScriptIndex;

            var builderInput = new AddressablesDataBuilderInput(context.Settings);

            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult rst, builderInput);

            if (string.IsNullOrEmpty(rst.Error) is false)
                L.E("Addressable content post-build failure.");
        }

        void OnCleanAll()
        {
            OnCleanAddressables(null);
            OnCleanSBP();
        }

        void OnCleanAddressables(object builder)
        {
            AddressableAssetSettings.CleanPlayerContent(builder as IDataBuilder);
        }

        void OnCleanSBP()
        {
            BuildCache.PurgeCache(true);
        }

#if (ENABLE_CCD && UNITY_2019_4_OR_NEWER)
        async void OnBuildAndRelease()
        {
            await AddressableAssetSettings.BuildAndReleasePlayerContent();
        }
#endif

        void OnBuildScript(object context)
        {
            OnSetActiveBuildScript(context);
            OnBuildPlayerData();
        }

        void OnBuildPlayerData()
        {
            AddressableAssetSettings.BuildPlayerContent();
        }

        void OnSetActiveBuildScript(object context)
        {
            AddressableAssetSettingsDefaultObject.Settings.ActivePlayerDataBuilderIndex = (byte) context;
        }

        void OnSetActivePlayModeScript(object context)
        {
            AddressableAssetSettingsDefaultObject.Settings.ActivePlayModeDataBuilderIndex = (byte) context;
        }

        bool m_ModificationRegistered;

        public void OnEnable()
        {
            if (AddressableAssetSettingsDefaultObject.Settings == null)
                return;
            if (!m_ModificationRegistered)
            {
                AddressableAssetSettingsDefaultObject.Settings.OnModification += OnSettingsModification;
                m_ModificationRegistered = true;
            }
        }

        public void OnDisable()
        {
            if (AddressableAssetSettingsDefaultObject.Settings == null)
                return;
            if (m_ModificationRegistered)
            {
                AddressableAssetSettingsDefaultObject.Settings.OnModification -= OnSettingsModification;
                m_ModificationRegistered = false;
            }
        }

        public bool OnGUI(Rect pos)
        {
            if (settings == null)
                return false;

            if (!m_ModificationRegistered)
            {
                m_ModificationRegistered = true;
                settings.OnModification -= OnSettingsModification; //just in case...
                settings.OnModification += OnSettingsModification;
            }

            if (m_EntryTree == null)
                InitialiseEntryTree();

            HandleVerticalResize(pos);
            var inRectY = pos.height;
            var searchRect = new Rect(pos.xMin, pos.yMin, pos.width, k_SearchHeight);
            var treeRect = new Rect(pos.xMin, pos.yMin + k_SearchHeight, pos.width, inRectY - k_SearchHeight);

            TopToolbar(searchRect);
            m_EntryTree.OnGUI(treeRect);
            return m_ResizingVerticalSplitter;
        }

        internal AddressableAssetEntryTreeView InitialiseEntryTree()
        {
            m_TreeState ??= new TreeViewState();

            var headerState = AddressableAssetEntryTreeView.CreateDefaultMultiColumnHeaderState();
            if (MultiColumnHeaderState.CanOverwriteSerializedFields(m_Mchs, headerState))
                MultiColumnHeaderState.OverwriteSerializedFields(m_Mchs, headerState);
            m_Mchs = headerState;

            m_SearchField = new SearchField();
            m_EntryTree = new AddressableAssetEntryTreeView(m_TreeState, m_Mchs, this);
            m_EntryTree.Reload();
            return m_EntryTree;
        }

        public void Reload()
        {
            if (m_EntryTree != null)
                m_EntryTree.Reload();
        }

        void HandleVerticalResize(Rect position)
        {
            m_VerticalSplitterRect.y = (int) (position.yMin + position.height * m_VerticalSplitterPercent);
            m_VerticalSplitterRect.width = position.width;
            m_VerticalSplitterRect.height = k_SplitterWidth;


            EditorGUIUtility.AddCursorRect(m_VerticalSplitterRect, MouseCursor.ResizeVertical);
            if (Event.current.type == EventType.MouseDown && m_VerticalSplitterRect.Contains(Event.current.mousePosition))
                m_ResizingVerticalSplitter = true;

            if (m_ResizingVerticalSplitter)
            {
                var mousePosInRect = Event.current.mousePosition.y - position.yMin;
                m_VerticalSplitterPercent = Mathf.Clamp(mousePosInRect / position.height, 0.20f, 0.90f);
                m_VerticalSplitterRect.y = (int) (position.height * m_VerticalSplitterPercent + position.yMin);

                if (Event.current.type == EventType.MouseUp)
                {
                    m_ResizingVerticalSplitter = false;
                }
            }
            else
                m_VerticalSplitterPercent = Mathf.Clamp(m_VerticalSplitterPercent, 0.20f, 0.90f);
        }
    }
}