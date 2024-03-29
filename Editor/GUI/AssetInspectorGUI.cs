using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.AddressableAssets.Util;
using UnityEngine.TestTools;

namespace UnityEditor.AddressableAssets.GUI
{
    using Object = UnityEngine.Object;

    [InitializeOnLoad, ExcludeFromCoverage]
    internal static class AddressableAssetInspectorGUI
    {
        static GUIStyle s_ToggleMixed;
        static GUIContent s_AddressableAssetToggleText;

        static GUIContent s_GroupsDropdownLabelContent = new GUIContent("Group", "The Addressable Group that this asset is assigned to.");

        static string s_GroupsDropdownControlName = nameof(AddressableAssetInspectorGUI) + ".GroupsPopupField";
        static Texture s_GroupsCaretTexture = null;
        static Texture s_FolderTexture = null;

        static AddressableAssetInspectorGUI()
        {
            s_ToggleMixed = null;
            s_AddressableAssetToggleText = new GUIContent("Addressable",
                "Check this to mark this asset as an Addressable Asset, which includes it in the bundled data and makes it loadable via script by its address.");
            Editor.finishedDefaultHeaderGUI += OnPostHeaderGUI;
        }

        static void SetAaEntry(AddressableAssetSettings aaSettings, List<TargetInfo> targetInfos, bool create)
        {
            Undo.RecordObject(aaSettings, "AddressableAssetSettings");

            if (!create)
            {
                var removedEntries = new List<AddressableAssetEntry>(targetInfos.Count);
                for (var i = 0; i < targetInfos.Count; ++i)
                {
                    var e = aaSettings.FindAssetEntry(targetInfos[i].Guid);
                    removedEntries.Add(e);
                    aaSettings.RemoveAssetEntry(removedEntries[i], false);
                }

                if (removedEntries.Count > 0)
                    aaSettings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryRemoved, removedEntries, true);
            }
            else
            {
                var parentGroup = aaSettings.DefaultGroup;
                var resourceTargets = targetInfos.Where(ti => AddressableAssetUtility.IsInResources(ti.Path));
                foreach (var resourceTarget in resourceTargets)
                    L.W($"Skipping marking resource asset '{resourceTarget.Path}' as addressable.");

                var otherTargetGuids  = targetInfos.Except(resourceTargets).Select(info => info.Guid).ToList();
                var entriesCreated = new List<AddressableAssetEntry>();
                var entriesMoved = new List<AddressableAssetEntry>();
                aaSettings.CreateOrMoveEntries(otherTargetGuids, parentGroup, entriesCreated, entriesMoved, false);

                if (entriesMoved.Count > 0)
                    aaSettings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entriesMoved, true);

                if (entriesCreated.Count > 0)
                    aaSettings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryAdded, entriesCreated, true);
            }
        }

        [UnityEngine.TestTools.ExcludeFromCoverage]
        static void OnPostHeaderGUI(Editor editor)
        {
            var targets = editor.targets;
            if (targets.Length == 0)
                return;

            // only display for the Prefab/Model importer not the displayed GameObjects
            if (targets[0] is GameObject)
                return;

            var aaSettings = AddressableDefaultSettings.Settings;

            var targetInfos = GatherTargetInfos(targets, aaSettings);
            if (targetInfos.Count == 0)
                return;

            var targetHasAddressableSubObject = false;
            var mainAssetsAddressable = 0;
            foreach (var info in targetInfos)
            {
                if (info.MainAssetEntry == null)
                    continue;
                mainAssetsAddressable++;
                if (!info.IsMainAsset)
                    targetHasAddressableSubObject = true;
            }

            // Overrides a DisabledScope in the EditorElement.cs that disables GUI drawn in the header when the asset cannot be edited.
            bool prevEnabledState = UnityEngine.GUI.enabled;
            if (targetHasAddressableSubObject)
                UnityEngine.GUI.enabled = false;
            else
            {
                UnityEngine.GUI.enabled = true;
                foreach (var info in targetInfos)
                {
                    if (!info.IsMainAsset)
                    {
                        UnityEngine.GUI.enabled = false;
                        break;
                    }
                }
            }

            int totalAddressableCount = mainAssetsAddressable;
            if (totalAddressableCount == 0) // nothing is addressable
            {
                if (GUILayout.Toggle(false, s_AddressableAssetToggleText, GUILayout.ExpandWidth(false)))
                    SetAaEntry(AddressableDefaultSettings.Settings, targetInfos, true);
            }
            else if (totalAddressableCount == editor.targets.Length) // everything is addressable
            {
                var entryInfo = targetInfos[targetInfos.Count - 1];
                if (entryInfo == null || entryInfo.MainAssetEntry == null)
                    throw new NullReferenceException("EntryInfo incorrect for Addressables content.");

                GUILayout.BeginHorizontal();

                if (mainAssetsAddressable > 0)
                {
                    if (!GUILayout.Toggle(true, s_AddressableAssetToggleText, GUILayout.ExpandWidth(false)))
                    {
                        SetAaEntry(aaSettings, targetInfos, false);
                        UnityEngine.GUI.enabled = prevEnabledState;
                        GUIUtility.ExitGUI();
                    }
                }
                else if (GUILayout.Toggle(false, s_AddressableAssetToggleText, GUILayout.ExpandWidth(false)))
                    SetAaEntry(aaSettings, targetInfos, true);

                if (editor.targets.Length == 1)
                {
                    if (!entryInfo.IsMainAsset)
                    {
                        bool preAddressPrevEnabledState = UnityEngine.GUI.enabled;
                        UnityEngine.GUI.enabled = false;
                        string address = entryInfo.Address + (entryInfo.IsMainAsset ? "" : $"[{entryInfo.TargetObject.name}]");
                        EditorGUILayout.DelayedTextField(address, GUILayout.ExpandWidth(true));
                        UnityEngine.GUI.enabled = preAddressPrevEnabledState;
                    }
                    else
                    {
                        string newAddress = EditorGUILayout.DelayedTextField(entryInfo.Address, GUILayout.ExpandWidth(true));
                        if (newAddress != entryInfo.Address) entryInfo.MainAssetEntry.address = newAddress;
                    }
                }
                else
                {
                    FindUniqueAssetGuids(targetInfos, out var uniqueAssetGuids, out var uniqueAddressableAssetGuids);
                    EditorGUILayout.LabelField(uniqueAddressableAssetGuids.Count + " out of " + uniqueAssetGuids.Count + " assets are addressable.");
                }

                DrawSelectEntriesButton(targetInfos);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                DrawGroupsDropdown(aaSettings, targetInfos);
                GUILayout.EndHorizontal();
            }
            else // mixed addressable selected
            {
                GUILayout.BeginHorizontal();
                if (s_ToggleMixed == null)
                    s_ToggleMixed = new GUIStyle("ToggleMixed");
                if (GUILayout.Toggle(false, s_AddressableAssetToggleText, s_ToggleMixed, GUILayout.ExpandWidth(false)))
                    SetAaEntry(AddressableDefaultSettings.Settings, targetInfos, true);
                FindUniqueAssetGuids(targetInfos, out var uniqueAssetGuids, out var uniqueAddressableAssetGuids);
                EditorGUILayout.LabelField(uniqueAddressableAssetGuids.Count + " out of " + uniqueAssetGuids.Count + " assets are addressable.");
                DrawSelectEntriesButton(targetInfos);
                GUILayout.EndHorizontal();
            }

            UnityEngine.GUI.enabled = prevEnabledState;
        }

        // Caching due to Gathering TargetInfos is an expensive operation
        // The InspectorGUI needs to call this multiple times per layout and paint
        static int s_TargetInfoLastHash = 0;
        static readonly List<TargetInfo> s_TargetInfoCache = new();

        internal static List<TargetInfo> GatherTargetInfos(Object[] targets, AddressableAssetSettings aaSettings)
        {
            var hash = targets[0].GetHashCode();
            for (var i = 1; i < targets.Length; ++i)
                hash = hash * 31 ^ targets[i].GetHashCode();

            if (hash == s_TargetInfoLastHash)
                return s_TargetInfoCache;

            s_TargetInfoLastHash = hash;
            s_TargetInfoCache.Clear();
            foreach (var t in targets)
            {
                if (AddressableAssetUtility.TryGetPathAndGUIDFromTarget(t, out var path, out var rawGuid) is false)
                    continue;

                var mainAssetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                // Is asset
                if (mainAssetType == null || mainAssetType == typeof(DefaultAsset) || BuildUtility.IsEditorType(mainAssetType))
                    continue;

                var guid = (AssetGUID) rawGuid;
                var isMainAsset = t is AssetImporter || AssetDatabase.IsMainAsset(t);
                var info = new TargetInfo() {TargetObject = t, Guid = guid, Path = path, IsMainAsset = isMainAsset};
                if (aaSettings != null)
                {
                    var entry = aaSettings.FindAssetEntry(guid);
                    if (entry != null)
                        info.MainAssetEntry = entry;
                }

                s_TargetInfoCache.Add(info);
            }

            return s_TargetInfoCache;
        }

        internal static void FindUniqueAssetGuids(List<TargetInfo> targetInfos, out HashSet<AssetGUID> uniqueAssetGuids, out HashSet<AssetGUID> uniqueAddressableAssetGuids)
        {
            uniqueAssetGuids = new HashSet<AssetGUID>();
            uniqueAddressableAssetGuids = new HashSet<AssetGUID>();
            foreach (TargetInfo info in targetInfos)
            {
                uniqueAssetGuids.Add(info.Guid);
                if (info.MainAssetEntry != null)
                    uniqueAddressableAssetGuids.Add(info.Guid);
            }
        }

        static void DrawSelectEntriesButton(List<TargetInfo> targets)
        {
            var prevGuiEnabled = UnityEngine.GUI.enabled;
            UnityEngine.GUI.enabled = true;

            if (GUILayout.Button("Select"))
            {
                AddressableAssetsWindow.Init();
                var window = EditorWindow.GetWindow<AddressableAssetsWindow>();
                List<AddressableAssetEntry> entries = new List<AddressableAssetEntry>(targets.Count);
                foreach (TargetInfo info in targets)
                {
                    if (info.MainAssetEntry != null)
                    {
                        entries.Add(info.MainAssetEntry);
                    }
                }

                if (entries.Count > 0)
                    window.SelectAssetsInGroupEditor(entries);
            }

            UnityEngine.GUI.enabled = prevGuiEnabled;
        }

        static void DrawGroupsDropdown(AddressableAssetSettings settings, List<TargetInfo> targets)
        {
            bool canEditGroup = true;
            bool mixedGroups = false;
            AddressableAssetGroup displayGroup = null;
            var entries = new List<AddressableAssetEntry>();
            foreach (TargetInfo info in targets)
            {
                AddressableAssetEntry entry = info.MainAssetEntry;
                if (entry == null)
                {
                    canEditGroup = false;
                }
                else
                {
                    entries.Add(entry);

                    if (displayGroup == null)
                        displayGroup = entry.parentGroup;
                    else if (entry.parentGroup != displayGroup)
                    {
                        mixedGroups = true;
                    }
                }
            }

            using (new EditorGUI.DisabledScope(!canEditGroup))
            {
                GUILayout.Label(s_GroupsDropdownLabelContent);
                if (mixedGroups)
                    EditorGUI.showMixedValue = true;

                UnityEngine.GUI.SetNextControlName(s_GroupsDropdownControlName);

                float iconHeight = EditorGUIUtility.singleLineHeight - EditorGUIUtility.standardVerticalSpacing * 3;
                Vector2 iconSize = EditorGUIUtility.GetIconSize();
                EditorGUIUtility.SetIconSize(new Vector2(iconHeight, iconHeight));
                if (s_FolderTexture == null)
                {
                    s_FolderTexture = EditorGUIUtility.IconContent("Folder Icon").image;
                }

                GUIContent groupGUIContent = new GUIContent(displayGroup.Name, s_FolderTexture);
                Rect groupFieldRect = GUILayoutUtility.GetRect(groupGUIContent, EditorStyles.objectField);
                EditorGUI.DropdownButton(groupFieldRect, groupGUIContent, FocusType.Keyboard, EditorStyles.objectField);
                EditorGUIUtility.SetIconSize(new Vector2(iconSize.x, iconSize.y));

                if (mixedGroups)
                    EditorGUI.showMixedValue = false;

                float pickerWidth = 12f;
                Rect groupFieldRectNoPicker = new Rect(groupFieldRect);
                groupFieldRectNoPicker.xMax = groupFieldRect.xMax - pickerWidth * 1.33f;

                Rect pickerRect = new Rect(groupFieldRectNoPicker.xMax, groupFieldRectNoPicker.y, pickerWidth, groupFieldRectNoPicker.height);
                bool isPickerPressed = Event.current.clickCount == 1 && pickerRect.Contains(Event.current.mousePosition);

                DrawCaret(pickerRect);

                if (canEditGroup)
                {
                    bool isEnterKeyPressed = Event.current.type == EventType.KeyDown && Event.current.isKey && (Event.current.keyCode == KeyCode.KeypadEnter || Event.current.keyCode == KeyCode.Return);
                    bool enterKeyRequestsPopup = isEnterKeyPressed && (s_GroupsDropdownControlName == UnityEngine.GUI.GetNameOfFocusedControl());
                    if (isPickerPressed || enterKeyRequestsPopup)
                    {
                        EditorWindow.GetWindow<GroupsPopupWindow>(true, "Select Addressable Group").Initialize(settings, entries, !mixedGroups, true, Event.current.mousePosition, AddressableAssetUtility.MoveEntriesToGroup);
                    }

                    bool isDragging = Event.current.type == EventType.DragUpdated && groupFieldRectNoPicker.Contains(Event.current.mousePosition);
                    bool isDropping = Event.current.type == EventType.DragPerform && groupFieldRectNoPicker.Contains(Event.current.mousePosition);
                    HandleDragAndDrop(settings, entries, isDragging, isDropping);
                }

                if (!mixedGroups)
                {
                    if (Event.current.clickCount == 1 && groupFieldRectNoPicker.Contains(Event.current.mousePosition))
                    {
                        UnityEngine.GUI.FocusControl(s_GroupsDropdownControlName);
                        AddressableAssetsWindow.Init();
                        var window = EditorWindow.GetWindow<AddressableAssetsWindow>();
                        window.SelectGroupInGroupEditor(displayGroup, false);
                    }

                    if (Event.current.clickCount == 2 && groupFieldRectNoPicker.Contains(Event.current.mousePosition))
                    {
                        AddressableAssetsWindow.Init();
                        var window = EditorWindow.GetWindow<AddressableAssetsWindow>();
                        window.SelectGroupInGroupEditor(displayGroup, true);
                    }
                }
            }
        }

        static void DrawCaret(Rect pickerRect)
        {
            if (s_GroupsCaretTexture == null)
            {
                s_GroupsCaretTexture = EditorGUIUtility.IconContent("d_pick").image;
            }
            UnityEngine.GUI.DrawTexture(pickerRect, s_GroupsCaretTexture, ScaleMode.ScaleToFit);
        }

        static void HandleDragAndDrop(AddressableAssetSettings settings, List<AddressableAssetEntry> aaEntries, bool isDragging, bool isDropping)
        {
            var groupItems = DragAndDrop.GetGenericData("AssetEntryTreeViewItem") as List<AssetEntryTreeViewItem>;
            if (isDragging)
            {
                bool canDragGroup = groupItems != null && groupItems.Count == 1 && groupItems[0].IsGroup;
                DragAndDrop.visualMode = canDragGroup ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            }
            else if (isDropping)
            {
                if (groupItems != null)
                {
                    var group = groupItems[0].group;
                    AddressableAssetUtility.MoveEntriesToGroup(settings, aaEntries, group);
                }
            }
        }

        internal class TargetInfo
        {
            public Object TargetObject;
            public AssetGUID Guid;
            public string Path;
            public bool IsMainAsset;
            public AddressableAssetEntry MainAssetEntry;

            public string Address
            {
                get
                {
                    if (MainAssetEntry == null)
                        throw new NullReferenceException("No Entry set for Target info with AssetPath " + Path);
                    return MainAssetEntry.address;
                }
            }
        }
    }
}
