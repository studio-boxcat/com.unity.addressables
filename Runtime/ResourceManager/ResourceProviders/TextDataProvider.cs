using System;
using System.ComponentModel;
using System.IO;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.Exceptions;
using UnityEngine.ResourceManagement.Util;

namespace UnityEngine.ResourceManagement.ResourceProviders
{
    /// <summary>
    /// Provides raw text from a local or remote URL.
    /// </summary>
    [DisplayName("Text Data Provider")]
    public class TextDataProvider : ResourceProviderBase
    {
        /// <summary>
        /// Controls whether errors are logged - this is disabled when trying to load from the local cache since failures are expected
        /// </summary>
        public bool IgnoreFailures { get; set; }

        internal class InternalOp
        {
            TextDataProvider m_Provider;
            UnityWebRequestAsyncOperation m_RequestOperation;
            ProvideHandle m_PI;
            bool m_IgnoreFailures;
            private bool m_Complete = false;

            public void Start(ProvideHandle provideHandle, TextDataProvider rawProvider)
            {
                m_PI = provideHandle;
                m_PI.SetWaitForCompletionCallback(WaitForCompletionHandler);
                m_Provider = rawProvider;

                // override input options with options from Location if included
                if (m_PI.Location.Data is ProviderLoadRequestOptions providerData)
                {
                    m_IgnoreFailures = providerData.IgnoreFailures;
                }
                else
                {
                    m_IgnoreFailures = rawProvider.IgnoreFailures;
                }

                var path = m_PI.Location.InternalId;
                if (ResourceManagerConfig.ShouldPathUseWebRequest(path))
                {
                    SendWebRequest(path);
                }
                else if (File.Exists(path))
                {
#if NET_4_6
                    if (path.Length >= 260)
                        path = @"\\?\" + path;
#endif
                    var text = File.ReadAllText(path);
                    object result = ConvertText(text);
                    m_PI.Complete(result, result != null, result == null ? new Exception($"Unable to load asset of type {m_PI.Type} from location {m_PI.Location}.") : null);
                    m_Complete = true;
                }
                else
                {
                    Exception exception = null;
                    //Don't log errors when loading from the persistentDataPath since these files are expected to not exist until created
                    if (m_IgnoreFailures)
                    {
                        m_PI.Complete<object>(null, true, exception);
                        m_Complete = true;
                    }
                    else
                    {
                        exception = new Exception(string.Format("Invalid path in " + nameof(TextDataProvider) + " : '{0}'.", path));
                        m_PI.Complete<object>(null, false, exception);
                        m_Complete = true;
                    }
                }
            }

            bool WaitForCompletionHandler()
            {
                if (m_Complete)
                    return true;

                if (m_RequestOperation != null)
                {
                    if (m_RequestOperation.isDone && !m_Complete)
                        RequestOperation_completed(m_RequestOperation);
                    else if (!m_RequestOperation.isDone)
                        return false;
                }

                return m_Complete;
            }

            private void RequestOperation_completed(AsyncOperation op)
            {
                if (m_Complete)
                    return;

                var webOp = op as UnityWebRequestAsyncOperation;
                string textResult = null;
                Exception exception = null;
                if (webOp != null)
                {
                    var webReq = webOp.webRequest;
                    if (!UnityWebRequestUtilities.RequestHasErrors(webReq))
                        textResult = webReq.downloadHandler.text;
                    else
                        exception = new ProviderException($"{nameof(TextDataProvider)} : unable to load from url : {webReq.url}", m_PI.Location);
                    webReq.Dispose();
                }
                else
                {
                    exception = new ProviderException(nameof(TextDataProvider) + " unable to load from unknown url", m_PI.Location);
                }

                CompleteOperation(textResult, exception);
            }

            protected void CompleteOperation(string text, Exception exception)
            {
                object result = null;
                if (!string.IsNullOrEmpty(text))
                    result = ConvertText(text);

                m_PI.Complete(result, result != null || m_IgnoreFailures, exception);
                m_Complete = true;
            }

            private object ConvertText(string text)
            {
                try
                {
                    return m_Provider.Convert(m_PI.Type, text);
                }
                catch (Exception e)
                {
                    if (!m_IgnoreFailures)
                        Debug.LogException(e);
                    return null;
                }
            }

            protected virtual void SendWebRequest(string path)
            {
                path = path.Replace('\\', '/');
                UnityWebRequest request = new UnityWebRequest(path, UnityWebRequest.kHttpVerbGET, new DownloadHandlerBuffer(), null);
                m_RequestOperation = request.SendWebRequest();
                if (m_RequestOperation.isDone)
                    RequestOperation_completed(m_RequestOperation);
                else
                    m_RequestOperation.completed += RequestOperation_completed;
            }
        }

        /// <summary>
        /// Method to convert the text into the object type requested.  Usually the text contains a JSON formatted serialized object.
        /// </summary>
        /// <param name="type">The object type the text is converted to.</param>
        /// <param name="text">The text to be converted.</param>
        /// <returns>The converted object.</returns>
        public virtual object Convert(Type type, string text)
        {
            return text;
        }

        /// <summary>
        /// Provides raw text data from the location.
        /// </summary>
        /// <param name="provideHandle">The data needed by the provider to perform the load.</param>
        public override void Provide(ProvideHandle provideHandle)
        {
            new InternalOp().Start(provideHandle, this);
        }
    }
}
