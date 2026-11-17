using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantumRealityShiftV4
{
    /// <summary>
    /// Handles all communication with the real Moth Atlas REST API
    /// (Base URL: https://api.mothquantum.com/api/v1).
    /// Workflow: POST /engines/{id}/process -> poll GET /jobs/{id}/status -> GET /jobs/{id}/result.
    /// File-output engines (e.g., entanglement-shader-v1) return presigned URLs fetched without auth headers.
    /// All calls are async — never blocks the GTA V game thread.
    /// </summary>
    public class AtlasService : IDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpClient _downloadClient;
        private readonly bool _hasApiKey;
        private const string BaseUrl = "https://api.mothquantum.com/api/v1";
        private const int PollIntervalMs = 1000;
        private const int MaxPollAttempts = 30;

        static AtlasService()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public AtlasService(string apiKey)
        {
            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromSeconds(15);

            // Dedicated client without Authorization header for presigned S3 asset downloads
            _downloadClient = new HttpClient();
            _downloadClient.Timeout = TimeSpan.FromSeconds(20);

            string cleanKey = apiKey?.Trim();
            if (!string.IsNullOrWhiteSpace(cleanKey) &&
                !string.Equals(cleanKey, "YOUR_ATLAS_API_KEY_HERE", StringComparison.OrdinalIgnoreCase))
            {
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cleanKey);
                _hasApiKey = true;
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
            _downloadClient?.Dispose();
        }

        /// <summary>
        /// Submits a job to an inline JSON->JSON Atlas engine, polls until completion,
        /// and returns (Result, JobId, Error).
        /// </summary>
        public async Task<(JToken Result, string JobId, string Error)> RunEngineJobAsync(string engineId, object paramsObj)
        {
            var (resultObj, jobId, err) = await SubmitPollAndFetchResultObjectAsync(engineId, paramsObj);
            if (err != null)
                return (null, jobId, err);

            JToken resToken = resultObj["result"];
            ModLogger.Api($"RES {engineId} job_id={jobId} result={(resToken != null ? resToken.ToString(Formatting.None) : "null")}");
            return (resToken, jobId, null);
        }

        /// <summary>
        /// Submits a job to a file-output Atlas engine (e.g., entanglement-shader-v1), polls until completion,
        /// and downloads outputs[0].url without an Authorization header.
        /// </summary>
        public async Task<(byte[] FileBytes, string JobId, string Error)> RunEngineFileJobAsync(string engineId, object paramsObj)
        {
            var (resultObj, jobId, err) = await SubmitPollAndFetchResultObjectAsync(engineId, paramsObj);
            if (err != null)
                return (null, jobId, err);

            try
            {
                JToken firstOut = resultObj["outputs"]?[0];
                string url = firstOut?["url"]?.ToString();
                if (string.IsNullOrWhiteSpace(url))
                {
                    ModLogger.Error($"RES_FILE {engineId} job_id={jobId} missing outputs[0].url");
                    return (null, jobId, "Missing outputs[0].url in Atlas result.");
                }

                string assetId = firstOut["output_asset_id"]?.ToString() ?? "unknown";
                string filename = firstOut["filename"]?.ToString() ?? "unknown";
                string contentType = firstOut["content_type"]?.ToString() ?? "unknown";
                string sizeBytes = firstOut["size_bytes"]?.ToString() ?? "0";

                var fileResp = await _downloadClient.GetAsync(url);
                if (!fileResp.IsSuccessStatusCode)
                {
                    ModLogger.Error($"RES_FILE {engineId} job_id={jobId} download HTTP {(int)fileResp.StatusCode}");
                    return (null, jobId, $"Asset download HTTP {(int)fileResp.StatusCode}");
                }

                byte[] bytes = await fileResp.Content.ReadAsByteArrayAsync();
                ModLogger.Api($"RES_FILE {engineId} job_id={jobId} asset_id={assetId} file={filename} type={contentType} meta_bytes={sizeBytes} downloaded_bytes={bytes.Length}");
                return (bytes, jobId, null);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"RES_FILE {engineId} job_id={jobId} exception={ex.Message}");
                return (null, jobId, ex.Message);
            }
        }

        private async Task<(JObject ResultBody, string JobId, string Error)> SubmitPollAndFetchResultObjectAsync(string engineId, object paramsObj)
        {
            if (!_hasApiKey)
            {
                ModLogger.Warn($"SKIP {engineId}: No MOTH_API_KEY / QuantumWeather.ini key configured.");
                return (null, null, "No MOTH_API_KEY / QuantumWeather.ini key configured.");
            }

            string jobId = null;
            try
            {
                var payload = JsonConvert.SerializeObject(new { @params = paramsObj });
                ModLogger.Api($"REQ {engineId} POST /engines/{engineId}/process payload={payload}");

                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var submitResp = await _client.PostAsync($"{BaseUrl}/engines/{engineId}/process", content);
                string submitBody = await submitResp.Content.ReadAsStringAsync();

                if (!submitResp.IsSuccessStatusCode)
                {
                    ModLogger.Error($"SUBMIT_ERR {engineId} HTTP {(int)submitResp.StatusCode}: {submitBody}");
                    return (null, null, $"HTTP {(int)submitResp.StatusCode}: {submitBody}");
                }

                var submitJson = JObject.Parse(submitBody);
                jobId = submitJson["job_id"]?.ToString();
                if (string.IsNullOrEmpty(jobId))
                {
                    ModLogger.Error($"SUBMIT_ERR {engineId} missing job_id: {submitBody}");
                    return (null, null, "Missing job_id in submit response.");
                }

                ModLogger.Api($"ACK {engineId} job_id={jobId} status={(int)submitResp.StatusCode}");

                for (int i = 0; i < MaxPollAttempts; i++)
                {
                    await Task.Delay(PollIntervalMs);
                    var statusResp = await _client.GetAsync($"{BaseUrl}/jobs/{jobId}/status");
                    if (!statusResp.IsSuccessStatusCode)
                        continue;

                    var statusJson = JObject.Parse(await statusResp.Content.ReadAsStringAsync());
                    string status = statusJson["status"]?.ToString();

                    if (status == "completed")
                    {
                        ModLogger.Api($"POLL {engineId} job_id={jobId} attempts={i + 1} status=completed");
                        break;
                    }

                    if (status == "failed" || status == "cancelled")
                    {
                        string errMsg = statusJson["error"]?["message"]?.ToString() ?? status;
                        ModLogger.Error($"POLL {engineId} job_id={jobId} attempts={i + 1} status={status} err={errMsg}");
                        return (null, jobId, $"Job {status}: {errMsg}");
                    }

                    if (i == MaxPollAttempts - 1)
                    {
                        ModLogger.Error($"POLL {engineId} job_id={jobId} timeout after {MaxPollAttempts} attempts");
                        return (null, jobId, "Timed out waiting for Atlas job.");
                    }
                }

                var resultResp = await _client.GetAsync($"{BaseUrl}/jobs/{jobId}/result");
                string resultBody = await resultResp.Content.ReadAsStringAsync();
                if (!resultResp.IsSuccessStatusCode)
                {
                    ModLogger.Error($"RESULT_ERR {engineId} job_id={jobId} HTTP {(int)resultResp.StatusCode}: {resultBody}");
                    return (null, jobId, $"Result HTTP {(int)resultResp.StatusCode}: {resultBody}");
                }

                var resultJson = JObject.Parse(resultBody);
                return (resultJson, jobId, null);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"EXCEPTION {engineId} job_id={jobId ?? "none"} msg={ex.Message}");
                return (null, jobId, ex.Message);
            }
        }
    }
}
