using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;

namespace UnityEngine.AddressableAssets.ResourceProviders
{
    /// <summary>
    /// Provider for content catalogs.  This provider makes use of a hash file to determine if a newer version of the catalog needs to be downloaded.
    /// </summary>
    [DisplayName("Content Catalog Provider")]
    public class ContentCatalogProvider : ResourceProviderBase
    {
        /// <summary>
        /// Options for specifying which entry in the catalog dependencies should hold each hash item.
        ///  The Remote should point to the hash on the server.  The Cache should point to the
        ///  local cache copy of the remote data.
        /// </summary>
        public enum DependencyHashIndex
        {
            /// <summary>
            /// Use to represent the index of the remote entry in the dependencies list.
            /// </summary>
            Remote = 0,

            /// <summary>
            /// Use to represent the index of the cache entry in the dependencies list.
            /// </summary>
            Cache,

            /// <summary>
            /// Use to represent the number of entries in the dependencies list.
            /// </summary>
            Count
        }

        internal Dictionary<IResourceLocation, InternalOp> m_LocationToCatalogLoadOpMap = new Dictionary<IResourceLocation, InternalOp>();

        /// <summary>
        /// Constructor for this provider.
        /// </summary>
        /// <param name="resourceManagerInstance">The resource manager to use.</param>
        public ContentCatalogProvider()
        {
            m_BehaviourFlags = ProviderBehaviourFlags.CanProvideWithFailedDependencies;
        }

        /// <inheritdoc/>
        public override void Release(IResourceLocation location, object obj)
        {
            if (m_LocationToCatalogLoadOpMap.ContainsKey(location))
            {
                m_LocationToCatalogLoadOpMap[location].Release();
                m_LocationToCatalogLoadOpMap.Remove(location);
            }

            base.Release(location, obj);
        }

        internal class InternalOp
        {
            //   int m_StartFrame;
            string m_LocalDataPath;
            ProvideHandle m_ProviderInterface;
            internal ContentCatalogData m_ContentCatalogData;
            AsyncOperationHandle<ContentCatalogData> m_ContentCatalogDataLoadOp;
            private bool m_Retried;

            public void Start(ProvideHandle providerInterface)
            {
                m_ProviderInterface = providerInterface;
                m_ProviderInterface.SetWaitForCompletionCallback(WaitForCompletionCallback);
                m_LocalDataPath = null;

                List<object> deps = new List<object>(); // TODO: garbage. need to pass actual count and reuse the list
                m_ProviderInterface.GetDependencies(deps);
                string idToLoad = DetermineIdToLoad(m_ProviderInterface.Location);

                Addressables.LogFormat("Addressables - Using content catalog from {0}.", idToLoad);

                LoadCatalog(idToLoad);
            }

            bool WaitForCompletionCallback()
            {
                if (m_ContentCatalogData != null)
                    return true;
                bool ccComplete;
                {
                    ccComplete = m_ContentCatalogDataLoadOp.IsDone;
                    if (!ccComplete)
                        m_ContentCatalogDataLoadOp.WaitForCompletion();
                }

                //content catalog op needs the Update to be pumped so we can invoke completion callbacks
                if (ccComplete && m_ContentCatalogData == null)
                    m_ProviderInterface.ResourceManager.Update(Time.unscaledDeltaTime);

                return ccComplete;
            }

            /// <summary>
            /// Clear all content catalog data.
            /// </summary>
            public void Release()
            {
                m_ContentCatalogData?.CleanData();
            }

            internal bool CanLoadCatalogFromBundle(string idToLoad, IResourceLocation location)
            {
                return Path.GetExtension(idToLoad) == ".bundle" &&
                       idToLoad.Equals(GetTransformedInternalId(location));
            }

            internal void LoadCatalog(string idToLoad)
            {
                try
                {
                    ProviderLoadRequestOptions providerLoadRequestOptions = null;
                    if (m_ProviderInterface.Location.Data is ProviderLoadRequestOptions providerData)
                        providerLoadRequestOptions = providerData.Copy();

                    {
#if ENABLE_BINARY_CATALOG
                        ResourceLocationBase location = new ResourceLocationBase(idToLoad, idToLoad,
                            typeof(BinaryAssetProvider<ContentCatalogData.Serializer>).FullName, typeof(ContentCatalogData));
                        location.Data = providerLoadRequestOptions;
                        m_ProviderInterface.ResourceManager.ResourceProviders.Add(new BinaryAssetProvider<ContentCatalogData.Serializer>());
#else
                        ResourceLocationBase location = new ResourceLocationBase(idToLoad, idToLoad,
                           typeof(JsonAssetProvider).FullName, typeof(ContentCatalogData));
#endif
                        m_ContentCatalogDataLoadOp = m_ProviderInterface.ResourceManager.ProvideResource<ContentCatalogData>(location);
                        m_ContentCatalogDataLoadOp.Completed += CatalogLoadOpCompleteCallback;
                    }
                }
                catch (Exception ex)
                {
                    m_ProviderInterface.Complete<ContentCatalogData>(null, false, ex);
                }
            }

            void CatalogLoadOpCompleteCallback(AsyncOperationHandle<ContentCatalogData> op)
            {
                m_ContentCatalogData = op.Result;
                m_ProviderInterface.ResourceManager.Release(op);
                OnCatalogLoaded(m_ContentCatalogData);
            }

            string GetTransformedInternalId(IResourceLocation loc)
            {
                return loc.InternalId;
            }

            const string kCatalogExt =
#if ENABLE_BINARY_CATALOG
            ".bin";
#else
            ".json";
#endif

            internal string DetermineIdToLoad(IResourceLocation location)
            {
                //default to load actual local source catalog
                return GetTransformedInternalId(location);
            }

            private void OnCatalogLoaded(ContentCatalogData ccd)
            {
                Addressables.LogFormat("Addressables - Content catalog load result = {0}.", ccd);
                if (ccd != null)
                {
#if ENABLE_ADDRESSABLE_PROFILER
                    ResourceManagement.Profiling.ProfilerRuntime.AddCatalog(Hash128.Parse(ccd.m_BuildResultHash));
#endif
                    ccd.location = m_ProviderInterface.Location;
#if ENABLE_CACHING
                    if (string.IsNullOrEmpty(m_LocalDataPath) && string.IsNullOrEmpty(Application.persistentDataPath))
                    {
                        Addressables.LogWarning($"Did not save cached content catalog because Application.persistentDataPath is an empty path.");
                    }
#endif

                    m_ProviderInterface.Complete(ccd, true, null);
                }
                else
                {
                    var errorMessage = $"Unable to load ContentCatalogData from location {m_ProviderInterface.Location}";
                    if (!m_Retried)
                    {
                        m_Retried = true;

                        //if the prev load path is cache, try to remove cache and reload from remote
                        var cachePath = GetTransformedInternalId(m_ProviderInterface.Location.Dependencies[(int)DependencyHashIndex.Cache]);
                        if (m_ContentCatalogDataLoadOp.LocationName == cachePath.Replace(".hash", kCatalogExt))
                        {
                            try
                            {
#if ENABLE_CACHING
                                File.Delete(cachePath);
#endif
                            }
                            catch (Exception)
                            {
                                errorMessage += $". Unable to delete cache data from location {cachePath}";
                                m_ProviderInterface.Complete(ccd, false, new Exception(errorMessage));
                                return;
                            }
                        }

                        Addressables.LogWarning(errorMessage + ". Attempting to retry...");
                        Start(m_ProviderInterface);
                    }
                    else
                    {
                        m_ProviderInterface.Complete(ccd, false, new Exception(errorMessage + " on second attempt."));
                    }
                }
            }
        }

        ///<inheritdoc/>
        public override void Provide(ProvideHandle providerInterface)
        {
            if (!m_LocationToCatalogLoadOpMap.ContainsKey(providerInterface.Location))
                m_LocationToCatalogLoadOpMap.Add(providerInterface.Location, new InternalOp());
            m_LocationToCatalogLoadOpMap[providerInterface.Location].Start(providerInterface);
        }
    }
}
