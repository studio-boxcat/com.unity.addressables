using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.Exceptions;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;
using UnityEngine.SceneManagement;

namespace UnityEngine.AddressableAssets
{
    internal class AddressablesImpl : IEqualityComparer<IResourceLocation>
    {
        ResourceManager m_ResourceManager;
        IInstanceProvider m_InstanceProvider;

        internal const string kCacheDataFolder = "{UnityEngine.Application.persistentDataPath}/com.unity.addressables/";

        public IInstanceProvider InstanceProvider
        {
            get { return m_InstanceProvider; }
            set
            {
                m_InstanceProvider = value;
                var rec = m_InstanceProvider as IUpdateReceiver;
                if (rec != null)
                    m_ResourceManager.AddUpdateReceiver(rec);
            }
        }

        public ISceneProvider SceneProvider;

        public ResourceManager ResourceManager
        {
            get
            {
                if (m_ResourceManager == null)
                    m_ResourceManager = new ResourceManager(new DefaultAllocationStrategy());
                return m_ResourceManager;
            }
        }

        internal List<ResourceLocatorInfo> m_ResourceLocators = new List<ResourceLocatorInfo>();
        AsyncOperationHandle<IResourceLocator> m_InitializationOperation;
        AsyncOperationHandle<List<string>> m_ActiveCheckUpdateOperation;
        internal AsyncOperationHandle<List<IResourceLocator>> m_ActiveUpdateOperation;


        Action<AsyncOperationHandle> m_OnHandleCompleteAction;
        Action<AsyncOperationHandle> m_OnSceneHandleCompleteAction;
        Action<AsyncOperationHandle> m_OnHandleDestroyedAction;
        Dictionary<object, AsyncOperationHandle> m_resultToHandle = new Dictionary<object, AsyncOperationHandle>();
        internal HashSet<AsyncOperationHandle> m_SceneInstances = new HashSet<AsyncOperationHandle>();

        internal int SceneOperationCount
        {
            get { return m_SceneInstances.Count; }
        }

        internal int TrackedHandleCount
        {
            get { return m_resultToHandle.Count; }
        }

        internal bool hasStartedInitialization = false;

        public AddressablesImpl(IAllocationStrategy alloc)
        {
            m_ResourceManager = new ResourceManager(alloc);
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        internal void ReleaseSceneManagerOperation()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        public AsyncOperationHandle ChainOperation
        {
            get
            {
                if (!hasStartedInitialization)
                    return InitializeAsync();
                if (m_InitializationOperation.IsValid() && !m_InitializationOperation.IsDone)
                    return m_InitializationOperation;
                if (m_ActiveUpdateOperation.IsValid() && !m_ActiveUpdateOperation.IsDone)
                    return m_ActiveUpdateOperation;
                Debug.LogWarning($"{nameof(ChainOperation)} property should not be accessed unless {nameof(ShouldChainRequest)} is true.");
                return default;
            }
        }

        internal bool ShouldChainRequest
        {
            get
            {
                if (!hasStartedInitialization)
                    return true;

                if (m_InitializationOperation.IsValid() && !m_InitializationOperation.IsDone)
                    return true;

                return m_ActiveUpdateOperation.IsValid() && !m_ActiveUpdateOperation.IsDone;
            }
        }

        internal void OnSceneUnloaded(Scene scene)
        {
            foreach (var s in m_SceneInstances)
            {
                if (!s.IsValid())
                {
                    m_SceneInstances.Remove(s);
                    break;
                }

                var sceneHandle = s.Convert<SceneInstance>();
                if (sceneHandle.Result.Scene == scene)
                {
                    m_SceneInstances.Remove(s);
                    m_resultToHandle.Remove(s.Result);

                    var op = SceneProvider.ReleaseScene(m_ResourceManager, sceneHandle);
                    AutoReleaseHandleOnCompletion(op);
                    break;
                }
            }

            m_ResourceManager.CleanupSceneInstances(scene);
        }

        public string StreamingAssetsSubFolder
        {
            get { return "aa"; }
        }

        public string BuildPath
        {
            get { return Addressables.LibraryPath + StreamingAssetsSubFolder + "/" + PlatformMappingService.GetPlatformPathSubFolder(); }
        }

        public string PlayerBuildDataPath
        {
            get { return Application.streamingAssetsPath + "/" + StreamingAssetsSubFolder; }
        }

        public string RuntimePath
        {
            get
            {
#if UNITY_EDITOR
                return BuildPath;
#else
                return PlayerBuildDataPath;
#endif
            }
        }

        public void Log(string msg)
        {
            Debug.Log(msg);
        }

        public void LogFormat(string format, params object[] args)
        {
            Debug.LogFormat(format, args);
        }

        public void LogWarning(string msg)
        {
            Debug.LogWarning(msg);
        }

        public void LogWarningFormat(string format, params object[] args)
        {
            Debug.LogWarningFormat(format, args);
        }

        public void LogError(string msg)
        {
            Debug.LogError(msg);
        }

        public void LogException(AsyncOperationHandle op, Exception ex)
        {
            if (op.Status == AsyncOperationStatus.Failed)
            {
                Debug.LogError(ex.ToString());
                Addressables.Log($"Failed op : {op.DebugName}");
            }
            else
                Addressables.Log(ex.ToString());
        }

        public void LogException(Exception ex)
        {
            Addressables.Log(ex.ToString());
        }

        public void LogErrorFormat(string format, params object[] args)
        {
            Debug.LogErrorFormat(format, args);
        }

        public string ResolveInternalId(string id)
        {
            var path = AddressablesRuntimeProperties.EvaluateString(id);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_WSA || UNITY_XBOXONE || UNITY_GAMECORE || UNITY_PS5 || UNITY_PS4 || UNITY_ANDROID
            if (path.Length >= 260 && path.StartsWith(Application.dataPath, StringComparison.Ordinal))
                path = path.Substring(Application.dataPath.Length + 1);
#endif
            return path;
        }

        public IEnumerable<IResourceLocator> ResourceLocators
        {
            get { return m_ResourceLocators.Select(l => l.Locator); }
        }

        public void AddResourceLocator(IResourceLocator loc, IResourceLocation remoteCatalogLocation = null)
        {
            m_ResourceLocators.Add(new ResourceLocatorInfo(loc, remoteCatalogLocation));
        }

        public void RemoveResourceLocator(IResourceLocator loc)
        {
            m_ResourceLocators.RemoveAll(l => l.Locator == loc);
        }

        public void ClearResourceLocators()
        {
            m_ResourceLocators.Clear();
        }

        internal bool GetResourceLocations(object key, Type type, out IList<IResourceLocation> locations)
        {
            if (type == null && (key is AssetReference))
                type = (key as AssetReference).SubOjbectType;

            key = EvaluateKey(key);

            locations = null;
            HashSet<IResourceLocation> current = null;
            foreach (var locatorInfo in m_ResourceLocators)
            {
                var locator = locatorInfo.Locator;
                IList<IResourceLocation> locs;
                if (locator.Locate(key, type, out locs))
                {
                    if (locations == null)
                    {
                        //simple, common case, no allocations
                        locations = locs;
                    }
                    else
                    {
                        //less common, need to merge...
                        if (current == null)
                        {
                            current = new HashSet<IResourceLocation>();
                            foreach (var loc in locations)
                                current.Add(loc);
                        }

                        current.UnionWith(locs);
                    }
                }
            }

            if (current == null)
                return locations != null;

            locations = new List<IResourceLocation>(current);
            return true;
        }

        public AsyncOperationHandle<IResourceLocator> InitializeAsync(string runtimeDataPath, string providerSuffix = null, bool autoReleaseHandle = true)
        {
            if (hasStartedInitialization)
            {
                if (m_InitializationOperation.IsValid())
                    return m_InitializationOperation;
                var completedOperation = ResourceManager.CreateCompletedOperation(m_ResourceLocators[0].Locator, errorMsg: null);
                if (autoReleaseHandle)
                    AutoReleaseHandleOnCompletion(completedOperation);
                return completedOperation;
            }

            if (ResourceManager.ExceptionHandler == null)
            {
                ResourceManager.ExceptionHandler = LogException;
            }

            hasStartedInitialization = true;
            if (m_InitializationOperation.IsValid())
                return m_InitializationOperation;
            //these need to be referenced in order to prevent stripping on IL2CPP platforms.
            GC.KeepAlive(Application.streamingAssetsPath);
#if !UNITY_SWITCH
            GC.KeepAlive(Application.persistentDataPath);
#endif
            if (string.IsNullOrEmpty(runtimeDataPath))
                return ResourceManager.CreateCompletedOperation<IResourceLocator>(null, string.Format("Invalid Key: {0}", runtimeDataPath));

            m_OnHandleCompleteAction = OnHandleCompleted;
            m_OnSceneHandleCompleteAction = OnSceneHandleCompleted;
            m_OnHandleDestroyedAction = OnHandleDestroyed;

#if UNITY_EDITOR
            Object settingsObject = null;
            string settingsPath = null;
            //this indicates that a specific addressables settings asset is being used for the runtime locations
            if (runtimeDataPath.StartsWith("GUID:", StringComparison.Ordinal))
                settingsPath = UnityEditor.AssetDatabase.GUIDToAssetPath(runtimeDataPath.Substring(runtimeDataPath.IndexOf(':') + 1));

            var assembly = Assembly.Load("Unity.Addressables.Editor");
            if (string.IsNullOrEmpty(settingsPath) && !UnityEditor.EditorApplication.isPlaying)
            {
                var rtp = runtimeDataPath.StartsWith("file://", StringComparison.Ordinal) ? runtimeDataPath.Substring("file://".Length) : runtimeDataPath;
                if (!File.Exists(rtp))
                {
                    var defaultSettingsObjectType = assembly.GetType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
                    var prop = defaultSettingsObjectType.GetProperty("DefaultAssetPath", BindingFlags.Public | BindingFlags.Static);
                    settingsPath = prop.GetValue(null) as string;
                    UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                }
            }

            if (!string.IsNullOrEmpty(settingsPath))
            {
                var settingsType = assembly.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings");
                settingsObject = UnityEditor.AssetDatabase.LoadAssetAtPath(settingsPath, settingsType);
                if (settingsObject != null)
                {
                    var settingsSetupMethod = settingsType.GetMethod("CreatePlayModeInitializationOperation", BindingFlags.Instance | BindingFlags.NonPublic);
                    m_InitializationOperation = (AsyncOperationHandle<IResourceLocator>)settingsSetupMethod.Invoke(settingsObject, new object[] {this});
                }
            }
#endif
            if (!m_InitializationOperation.IsValid())
                m_InitializationOperation = Initialization.InitializationOperation.CreateInitializationOperation(this, runtimeDataPath, providerSuffix);
            if (autoReleaseHandle)
                AutoReleaseHandleOnCompletion(m_InitializationOperation);

            return m_InitializationOperation;
        }

        public AsyncOperationHandle<IResourceLocator> InitializeAsync()
        {
            var settingsPath =
#if UNITY_EDITOR
                PlayerPrefs.GetString(Addressables.kAddressablesRuntimeDataPath, RuntimePath + "/settings.json");
#else
                RuntimePath + "/settings.json";
#endif

            return InitializeAsync(ResolveInternalId(settingsPath));
        }

        public AsyncOperationHandle<IResourceLocator> InitializeAsync(bool autoReleaseHandle)
        {
            var settingsPath =
#if UNITY_EDITOR
                PlayerPrefs.GetString(Addressables.kAddressablesRuntimeDataPath, RuntimePath + "/settings.json");
#else
                RuntimePath + "/settings.json";
#endif

            return InitializeAsync(ResolveInternalId(settingsPath), null, autoReleaseHandle);
        }

        public ResourceLocationBase CreateCatalogLocationWithHashDependencies<T>(IResourceLocation catalogLocation) where T : IResourceProvider
        {
            return CreateCatalogLocationWithHashDependencies<T>(catalogLocation.InternalId);
        }

        public ResourceLocationBase CreateCatalogLocationWithHashDependencies<T>(string catalogLocation) where T : IResourceProvider
        {
#if ENABLE_BINARY_CATALOG
            string hashFilePath = catalogLocation.Replace(".bin", ".hash");
#else
            string hashFilePath = catalogLocation.Replace(".json", ".hash");
#endif
            return CreateCatalogLocationWithHashDependencies<T>(catalogLocation, hashFilePath);
        }

        public ResourceLocationBase CreateCatalogLocationWithHashDependencies<T>(string catalogPath, string hashFilePath) where T : IResourceProvider
        {
            var catalogLoc = new ResourceLocationBase(catalogPath, catalogPath, typeof(T).FullName, typeof(IResourceLocator));
            catalogLoc.Data = new ProviderLoadRequestOptions()
            {
                IgnoreFailures = false,
            };

            if (!string.IsNullOrEmpty(hashFilePath))
            {
                ProviderLoadRequestOptions hashOptions = new ProviderLoadRequestOptions()
                {
                    IgnoreFailures = true,
                };

                string tmpPath = hashFilePath;
#if UNITY_SWITCH
                string cacheHashFilePath = hashFilePath; // ResourceLocationBase does not allow empty string id
#else
                // The file name of the local cached catalog + hash file is the hash code of the remote hash path, without query parameters (if any).
                string cacheHashFilePath = ResolveInternalId(kCacheDataFolder + tmpPath.GetHashCode() + ".hash");
#endif
                var hashResourceLocation = new ResourceLocationBase(hashFilePath, hashFilePath, typeof(TextDataProvider).FullName, typeof(string));
                hashResourceLocation.Data = hashOptions.Copy();
                catalogLoc.Dependencies.Add(hashResourceLocation);
                var cacheResourceLocation = new ResourceLocationBase(cacheHashFilePath, cacheHashFilePath, typeof(TextDataProvider).FullName, typeof(string));
                cacheResourceLocation.Data = hashOptions.Copy();
                catalogLoc.Dependencies.Add(cacheResourceLocation);
            }

            return catalogLoc;
        }

        [Conditional("UNITY_EDITOR")]
        void QueueEditorUpdateIfNeeded()
        {
#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying)
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        public AsyncOperationHandle<IResourceLocator> LoadContentCatalogAsync(string catalogPath, bool autoReleaseHandle = true, string providerSuffix = null)
        {
            var catalogLoc = CreateCatalogLocationWithHashDependencies<ContentCatalogProvider>(catalogPath);
            if (ShouldChainRequest)
                return ResourceManager.CreateChainOperation(ChainOperation, op => LoadContentCatalogAsync(catalogPath, autoReleaseHandle, providerSuffix));
            var handle = Initialization.InitializationOperation.LoadContentCatalog(this, catalogLoc, providerSuffix);
            if (autoReleaseHandle)
                AutoReleaseHandleOnCompletion(handle);
            QueueEditorUpdateIfNeeded();
            return handle;
        }

        AsyncOperationHandle<SceneInstance> TrackHandle(AsyncOperationHandle<SceneInstance> handle)
        {
            handle.Completed += (sceneHandle) => { m_OnSceneHandleCompleteAction(sceneHandle); };
            return handle;
        }

        AsyncOperationHandle<TObject> TrackHandle<TObject>(AsyncOperationHandle<TObject> handle)
        {
            handle.CompletedTypeless += m_OnHandleCompleteAction;
            return handle;
        }

        AsyncOperationHandle TrackHandle(AsyncOperationHandle handle)
        {
            handle.Completed += m_OnHandleCompleteAction;
            return handle;
        }

        internal void ClearTrackHandles()
        {
            m_resultToHandle.Clear();
        }

        public AsyncOperationHandle<TObject> LoadAssetAsync<TObject>(IResourceLocation location)
        {
            QueueEditorUpdateIfNeeded();
            if (ShouldChainRequest)
                return TrackHandle(LoadAssetWithChain<TObject>(ChainOperation, location));
            return TrackHandle(ResourceManager.ProvideResource<TObject>(location));
        }

        AsyncOperationHandle<TObject> LoadAssetWithChain<TObject>(AsyncOperationHandle dep, IResourceLocation loc)
        {
            return ResourceManager.CreateChainOperation(dep, op => LoadAssetAsync<TObject>(loc));
        }

        AsyncOperationHandle<TObject> LoadAssetWithChain<TObject>(AsyncOperationHandle dep, object key)
        {
            return ResourceManager.CreateChainOperation(dep, op => LoadAssetAsync<TObject>(key));
        }

        public AsyncOperationHandle<TObject> LoadAssetAsync<TObject>(object key)
        {
            QueueEditorUpdateIfNeeded();
            if (ShouldChainRequest)
                return TrackHandle(LoadAssetWithChain<TObject>(ChainOperation, key));

            key = EvaluateKey(key);

            IList<IResourceLocation> locs;
            var t = typeof(TObject);
            if (t.IsArray)
                t = t.GetElementType();
            else if (t.IsGenericType && typeof(IList<>) == t.GetGenericTypeDefinition())
                t = t.GetGenericArguments()[0];
            foreach (var locatorInfo in m_ResourceLocators)
            {
                var locator = locatorInfo.Locator;
                if (locator.Locate(key, t, out locs))
                {
                    foreach (var loc in locs)
                    {
                        var provider = ResourceManager.GetResourceProvider(typeof(TObject), loc);
                        if (provider != null)
                            return TrackHandle(ResourceManager.ProvideResource<TObject>(loc));
                    }
                }
            }

            return ResourceManager.CreateCompletedOperationWithException<TObject>(default(TObject), new InvalidKeyException(key, t, this));
        }

        class LoadResourceLocationKeyOp : AsyncOperationBase<IList<IResourceLocation>>
        {
            object m_Keys;
            IList<IResourceLocation> m_locations;
            AddressablesImpl m_Addressables;
            Type m_ResourceType;

            protected override string DebugName
            {
                get { return m_Keys.ToString(); }
            }

            public void Init(AddressablesImpl aa, Type t, object keys)
            {
                m_Keys = keys;
                m_ResourceType = t;
                m_Addressables = aa;
            }

            /// <inheritdoc />
            protected override bool InvokeWaitForCompletion()
            {
                m_RM?.Update(Time.unscaledDeltaTime);
                if (!HasExecuted)
                    InvokeExecute();
                return true;
            }

            protected override void Execute()
            {
                m_Addressables.GetResourceLocations(m_Keys, m_ResourceType, out m_locations);
                if (m_locations == null)
                    m_locations = new List<IResourceLocation>();
                Complete(m_locations, true, string.Empty);
            }
        }

        void OnHandleDestroyed(AsyncOperationHandle handle)
        {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                m_resultToHandle.Remove(handle.Result);
            }
        }

        void OnSceneHandleCompleted(AsyncOperationHandle handle)
        {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                m_SceneInstances.Add(handle);
                if (!m_resultToHandle.ContainsKey(handle.Result))
                {
                    handle.Destroyed += m_OnHandleDestroyedAction;
                    m_resultToHandle.Add(handle.Result, handle);
                }
            }
        }

        void OnHandleCompleted(AsyncOperationHandle handle)
        {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                if (!m_resultToHandle.ContainsKey(handle.Result))
                {
                    handle.Destroyed += m_OnHandleDestroyedAction;
                    m_resultToHandle.Add(handle.Result, handle);
                }
            }
        }

        public void Release<TObject>(TObject obj)
        {
            if (obj == null)
            {
                LogWarning("Addressables.Release() - trying to release null object.");
                return;
            }

            AsyncOperationHandle handle;
            if (m_resultToHandle.TryGetValue(obj, out handle))
                Release(handle);
            else
            {
                LogError("Addressables.Release was called on an object that Addressables was not previously aware of.  Thus nothing is being released");
            }
        }

        public void Release<TObject>(AsyncOperationHandle<TObject> handle)
        {
            if (typeof(TObject) == typeof(SceneInstance))
            {
                SceneInstance sceneInstance = (SceneInstance)Convert.ChangeType(handle.Result, typeof(SceneInstance));
                if (sceneInstance.Scene.isLoaded && handle.ReferenceCount == 1)
                {
                    if (SceneOperationCount == 1 && m_SceneInstances.First().Equals(handle))
                        m_SceneInstances.Clear();
                    UnloadSceneAsync(handle, UnloadSceneOptions.None, true);
                }
                else if (!sceneInstance.Scene.isLoaded && handle.ReferenceCount == 2 && !handle.UnloadSceneOpExcludeReleaseCallback)
                {
                    AutoReleaseHandleOnCompletion(handle);
                }
            }

            m_ResourceManager.Release(handle);
        }

        public void Release(AsyncOperationHandle handle)
        {
            m_ResourceManager.Release(handle);
        }

        static List<IResourceLocation> GatherDependenciesFromLocations(IList<IResourceLocation> locations)
        {
            var locHash = new HashSet<IResourceLocation>();
            foreach (var loc in locations)
            {
                if (loc.ResourceType == typeof(IAssetBundleResource))
                {
                    locHash.Add(loc);
                }

                if (loc.HasDependencies)
                {
                    foreach (var dep in loc.Dependencies)
                        if (dep.ResourceType == typeof(IAssetBundleResource))
                            locHash.Add(dep);
                }
            }

            return new List<IResourceLocation>(locHash);
        }

        internal void AutoReleaseHandleOnCompletion(AsyncOperationHandle handle)
        {
            handle.Completed += op => Release(op);
        }

        internal void AutoReleaseHandleOnCompletion<TObject>(AsyncOperationHandle<TObject> handle)
        {
            handle.Completed += op => Release(op);
        }

        internal void AutoReleaseHandleOnCompletion<TObject>(AsyncOperationHandle<TObject> handle, bool unloadSceneOpExcludeReleaseCallback)
        {
            handle.Completed += op =>
            {
                if (unloadSceneOpExcludeReleaseCallback)
                    op.UnloadSceneOpExcludeReleaseCallback = true;
                Release(op);
            };
        }

        internal void AutoReleaseHandleOnTypelessCompletion<TObject>(AsyncOperationHandle<TObject> handle)
        {
            handle.CompletedTypeless += op => Release(op);
        }

        public AsyncOperationHandle<GameObject> InstantiateAsync(IResourceLocation location, Transform parent = null, bool instantiateInWorldSpace = false, bool trackHandle = true)
        {
            return InstantiateAsync(location, new InstantiationParameters(parent, instantiateInWorldSpace), trackHandle);
        }

        public AsyncOperationHandle<GameObject> InstantiateAsync(IResourceLocation location, Vector3 position, Quaternion rotation, Transform parent = null, bool trackHandle = true)
        {
            return InstantiateAsync(location, new InstantiationParameters(position, rotation, parent), trackHandle);
        }

        public AsyncOperationHandle<GameObject> InstantiateAsync(object key, Transform parent = null, bool instantiateInWorldSpace = false, bool trackHandle = true)
        {
            return InstantiateAsync(key, new InstantiationParameters(parent, instantiateInWorldSpace), trackHandle);
        }

        public AsyncOperationHandle<GameObject> InstantiateAsync(object key, Vector3 position, Quaternion rotation, Transform parent = null, bool trackHandle = true)
        {
            return InstantiateAsync(key, new InstantiationParameters(position, rotation, parent), trackHandle);
        }

        AsyncOperationHandle<GameObject> InstantiateWithChain(AsyncOperationHandle dep, object key, InstantiationParameters instantiateParameters, bool trackHandle = true)
        {
            var chainOp = ResourceManager.CreateChainOperation(dep, op => InstantiateAsync(key, instantiateParameters, false));
            if (trackHandle)
                chainOp.CompletedTypeless += m_OnHandleCompleteAction;
            return chainOp;
        }

        public AsyncOperationHandle<GameObject> InstantiateAsync(object key, InstantiationParameters instantiateParameters, bool trackHandle = true)
        {
            QueueEditorUpdateIfNeeded();
            if (ShouldChainRequest)
                return InstantiateWithChain(ChainOperation, key, instantiateParameters, trackHandle);

            key = EvaluateKey(key);
            IList<IResourceLocation> locs;
            foreach (var locatorInfo in m_ResourceLocators)
            {
                var locator = locatorInfo.Locator;
                if (locator.Locate(key, typeof(GameObject), out locs))
                    return InstantiateAsync(locs[0], instantiateParameters, trackHandle);
            }

            return ResourceManager.CreateCompletedOperationWithException<GameObject>(null, new InvalidKeyException(key, typeof(GameObject), this));
        }

        AsyncOperationHandle<GameObject> InstantiateWithChain(AsyncOperationHandle dep, IResourceLocation location, InstantiationParameters instantiateParameters, bool trackHandle = true)
        {
            var chainOp = ResourceManager.CreateChainOperation(dep, op => InstantiateAsync(location, instantiateParameters, false));
            if (trackHandle)
                chainOp.CompletedTypeless += m_OnHandleCompleteAction;
            return chainOp;
        }

        public AsyncOperationHandle<GameObject> InstantiateAsync(IResourceLocation location, InstantiationParameters instantiateParameters, bool trackHandle = true)
        {
            QueueEditorUpdateIfNeeded();
            if (ShouldChainRequest)
                return InstantiateWithChain(ChainOperation, location, instantiateParameters, trackHandle);

            var opHandle = ResourceManager.ProvideInstance(InstanceProvider, location, instantiateParameters);
            if (!trackHandle)
                return opHandle;
            opHandle.CompletedTypeless += m_OnHandleCompleteAction;
            return opHandle;
        }

        public bool ReleaseInstance(GameObject instance)
        {
            if (instance == null)
            {
                LogWarning("Addressables.ReleaseInstance() - trying to release null object.");
                return false;
            }

            AsyncOperationHandle handle;
            if (m_resultToHandle.TryGetValue(instance, out handle))
                Release(handle);
            else
                return false;

            return true;
        }

        internal AsyncOperationHandle<SceneInstance> LoadSceneWithChain(AsyncOperationHandle dep, object key, LoadSceneParameters loadSceneParameters, bool activateOnLoad = true, int priority = 100)
        {
            return TrackHandle(ResourceManager.CreateChainOperation(dep, op => LoadSceneAsync(key, loadSceneParameters, activateOnLoad, priority, false)));
        }

        internal AsyncOperationHandle<SceneInstance> LoadSceneWithChain(AsyncOperationHandle dep, IResourceLocation key, LoadSceneMode loadMode = LoadSceneMode.Single, bool activateOnLoad = true,
            int priority = 100)
        {
            return TrackHandle(ResourceManager.CreateChainOperation(dep, op => LoadSceneAsync(key, loadMode, activateOnLoad, priority, false)));
        }

        public AsyncOperationHandle<SceneInstance> LoadSceneAsync(object key)
        {
            return LoadSceneAsync(key, new LoadSceneParameters(LoadSceneMode.Single));
        }

        public AsyncOperationHandle<SceneInstance> LoadSceneAsync(object key, LoadSceneParameters loadSceneParameters, bool activateOnLoad = true, int priority = 100, bool trackHandle = true)
        {
            QueueEditorUpdateIfNeeded();
            if (ShouldChainRequest)
                return LoadSceneWithChain(ChainOperation, key, loadSceneParameters, activateOnLoad, priority);

            IList<IResourceLocation> locations;
            if (!GetResourceLocations(key, typeof(SceneInstance), out locations))
                return ResourceManager.CreateCompletedOperationWithException<SceneInstance>(default(SceneInstance), new InvalidKeyException(key, typeof(SceneInstance), this));

            return LoadSceneAsync(locations[0], loadSceneParameters, activateOnLoad, priority, trackHandle);
        }

        public AsyncOperationHandle<SceneInstance> LoadSceneAsync(IResourceLocation location)
        {
            return LoadSceneAsync(location, new LoadSceneParameters(LoadSceneMode.Single));
        }

        public AsyncOperationHandle<SceneInstance> LoadSceneAsync(IResourceLocation location, LoadSceneMode loadMode = LoadSceneMode.Single, bool activateOnLoad = true, int priority = 100,
            bool trackHandle = true)
        {
            return LoadSceneAsync(location, new LoadSceneParameters(loadMode), activateOnLoad, priority, trackHandle);
        }

        public AsyncOperationHandle<SceneInstance> LoadSceneAsync(IResourceLocation location, LoadSceneParameters loadSceneParameters, bool activateOnLoad = true, int priority = 100,
            bool trackHandle = true)
        {
            QueueEditorUpdateIfNeeded();
            if (ShouldChainRequest)
                return LoadSceneWithChain(ChainOperation, location, loadSceneParameters, activateOnLoad, priority);

            var handle = ResourceManager.ProvideScene(SceneProvider, location, loadSceneParameters, activateOnLoad, priority);
            if (trackHandle)
                return TrackHandle(handle);

            return handle;
        }

        public AsyncOperationHandle<SceneInstance> UnloadSceneAsync(SceneInstance scene, UnloadSceneOptions unloadOptions = UnloadSceneOptions.None, bool autoReleaseHandle = true)
        {
            AsyncOperationHandle handle;
            if (!m_resultToHandle.TryGetValue(scene, out handle))
            {
                var msg = string.Format("Addressables.UnloadSceneAsync() - Cannot find handle for scene {0}", scene);
                LogWarning(msg);
                return ResourceManager.CreateCompletedOperation<SceneInstance>(scene, msg);
            }

            if (handle.m_InternalOp.IsRunning)
                return CreateUnloadSceneWithChain(handle, unloadOptions, autoReleaseHandle);

            return UnloadSceneAsync(handle, unloadOptions, autoReleaseHandle);
        }

        public AsyncOperationHandle<SceneInstance> UnloadSceneAsync(AsyncOperationHandle handle, UnloadSceneOptions unloadOptions = UnloadSceneOptions.None, bool autoReleaseHandle = true)
        {
            QueueEditorUpdateIfNeeded();
            if (handle.m_InternalOp.IsRunning)
                return CreateUnloadSceneWithChain(handle, unloadOptions, autoReleaseHandle);

            return UnloadSceneAsync(handle.Convert<SceneInstance>(), unloadOptions, autoReleaseHandle);
        }

        public AsyncOperationHandle<SceneInstance> UnloadSceneAsync(AsyncOperationHandle<SceneInstance> handle, UnloadSceneOptions unloadOptions = UnloadSceneOptions.None,
            bool autoReleaseHandle = true)
        {
            if (handle.m_InternalOp.IsRunning)
                return CreateUnloadSceneWithChain(handle, unloadOptions, autoReleaseHandle);

            return InternalUnloadScene(handle, unloadOptions, autoReleaseHandle);
        }

        internal AsyncOperationHandle<SceneInstance> CreateUnloadSceneWithChain(AsyncOperationHandle handle, UnloadSceneOptions unloadOptions, bool autoReleaseHandle)
        {
            return m_ResourceManager.CreateChainOperation(handle, (completedHandle) => InternalUnloadScene(completedHandle.Convert<SceneInstance>(), unloadOptions, autoReleaseHandle));
        }

        internal AsyncOperationHandle<SceneInstance> CreateUnloadSceneWithChain(AsyncOperationHandle<SceneInstance> handle, UnloadSceneOptions unloadOptions, bool autoReleaseHandle)
        {
            return m_ResourceManager.CreateChainOperation(handle, (completedHandle) => InternalUnloadScene(completedHandle, unloadOptions, autoReleaseHandle));
        }

        internal AsyncOperationHandle<SceneInstance> InternalUnloadScene(AsyncOperationHandle<SceneInstance> handle, UnloadSceneOptions unloadOptions, bool autoReleaseHandle)
        {
            QueueEditorUpdateIfNeeded();
            var relOp = SceneProvider.ReleaseScene(ResourceManager, handle, unloadOptions);
            if (autoReleaseHandle)
                AutoReleaseHandleOnCompletion(relOp, true);
            return relOp;
        }

        private object EvaluateKey(object obj)
        {
            if (obj is IKeyEvaluator)
                return (obj as IKeyEvaluator).RuntimeKey;
            return obj;
        }

        public ResourceLocatorInfo GetLocatorInfo(string c)
        {
            foreach (var l in m_ResourceLocators)
                if (l.Locator.LocatorId == c)
                    return l;
            return null;
        }

        //needed for IEqualityComparer<IResourceLocation> interface
        public bool Equals(IResourceLocation x, IResourceLocation y)
        {
            return x.PrimaryKey.Equals(y.PrimaryKey) && x.ResourceType.Equals(y.ResourceType) && x.InternalId.Equals(y.InternalId);
        }

        //needed for IEqualityComparer<IResourceLocation> interface
        public int GetHashCode(IResourceLocation loc)
        {
            return loc.PrimaryKey.GetHashCode() * 31 + loc.ResourceType.GetHashCode();
        }
    }
}
