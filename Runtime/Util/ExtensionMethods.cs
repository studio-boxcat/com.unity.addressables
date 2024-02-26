using System;
using System.Threading;
using UnityEngine.Assertions;
using UnityEngine.Networking;

namespace UnityEngine.AddressableAssets.Util
{
    public static class ExtensionMethods
    {
        public static void SafeInvoke<T>(this Action<T> callback, T arg)
        {
            Assert.IsNotNull(callback, "Callback is null");

            try
            {
                callback.Invoke(arg);
            }
            catch (Exception e)
            {
                L.Exception(e);
            }
        }

        public static void WaitForComplete(this UnityWebRequestAsyncOperation op)
        {
#if DEBUG
            var timeout = DateTime.Now.AddSeconds(1);
            while (op.isDone is false)
            {
                if (DateTime.Now > timeout)
                    throw new TimeoutException("Operation did not complete in time: " + op);
            }
#else
            while (op.isDone is false) ;
#endif
        }

        public static AssetBundle WaitForComplete(this AssetBundleCreateRequest req)
        {
            Assert.IsFalse(req.isDone, "Operation is already done");
            return req.assetBundle; // accessing asset before isDone is true will stall the loading process.
        }
    }
}