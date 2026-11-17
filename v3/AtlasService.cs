using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantumRealityShiftV3
{
    /// <summary>
    /// Handles all communication with the real Moth Atlas REST API
    /// (Base URL: https://api.mothquantum.com/api/v1).
    /// Workflow: POST /engines/{id}/process -> poll GET /jobs/{id}/status -> GET /jobs/{id}/result.
    /// File-output engines (e.g., entanglement-shader-v1) return presigned URLs fetched without auth headers.
    /// All calls are async — never blocks the GTA V game thread.
    /// </summary>
    public class AtlasService
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

        /// <summary>
        /// Submits a job to an inline JSON->JSON Atlas engine, polls until completion,
        /// and returns (Result, JobId, Error).
        /// </summary>
        public async Task<(JToken Result, string JobId, string Error)> RunEngineJobAsync(string engineId, object paramsObj)
        {
            var (resultObj, jobId, err) = await SubmitPollAndFetchResultObjectAsync(engineId, paramsObj);
            if (err != null)
                return (null, jobId, err);

            return (resultObj["result"], jobId, null);
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
                string url = resultObj["outputs"]?[0]?["url"]?.ToString();
                if (string.IsNullOrWhiteSpace(url))
                    return (null, jobId, "Missing outputs[0].url in Atlas result.");

                var fileResp = await _downloadClient.GetAsync(url);
                if (!fileResp.IsSuccessStatusCode)
                    return (null, jobId, $"Asset download HTTP {(int)fileResp.StatusCode}");

                byte[] bytes = await fileResp.Content.ReadAsByteArrayAsync();
                return (bytes, jobId, null);
            }
            catch (Exception ex)
            {
                return (null, jobId, ex.Message);
            }
        }

        private async Task<(JObject ResultBody, string JobId, string Error)> SubmitPollAndFetchResultObjectAsync(string engineId, object paramsObj)
        {
            if (!_hasApiKey)
                return (null, null, "No MOTH_API_KEY / QuantumWeather.ini key configured.");

            try
            {
                var payload = JsonConvert.SerializeObject(new { @params = paramsObj });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var submitResp = await _client.PostAsync($"{BaseUrl}/engines/{engineId}/process", content);

                if (!submitResp.IsSuccessStatusCode)
                {
                    string errBody = await submitResp.Content.ReadAsStringAsync();
                    return (null, null, $"HTTP {(int)submitResp.StatusCode}: {errBody}");
                }

                var submitJson = JObject.Parse(await submitResp.Content.ReadAsStringAsync());
                string jobId = submitJson["job_id"]?.ToString();
                if (string.IsNullOrEmpty(jobId))
                    return (null, null, "Missing job_id in submit response.");

                for (int i = 0; i < MaxPollAttempts; i++)
                {
                    await Task.Delay(PollIntervalMs);
                    var statusResp = await _client.GetAsync($"{BaseUrl}/jobs/{jobId}/status");
                    if (!statusResp.IsSuccessStatusCode)
                        continue;

                    var statusJson = JObject.Parse(await statusResp.Content.ReadAsStringAsync());
                    string status = statusJson["status"]?.ToString();

                    if (status == "completed")
                        break;

                    if (status == "failed" || status == "cancelled")
                    {
                        string errMsg = statusJson["error"]?["message"]?.ToString() ?? status;
                        return (null, jobId, $"Job {status}: {errMsg}");
                    }

                    if (i == MaxPollAttempts - 1)
                        return (null, jobId, "Timed out waiting for Atlas job.");
                }

                var resultResp = await _client.GetAsync($"{BaseUrl}/jobs/{jobId}/result");
                if (!resultResp.IsSuccessStatusCode)
                {
                    string errBody = await resultResp.Content.ReadAsStringAsync();
                    return (null, jobId, $"Result HTTP {(int)resultResp.StatusCode}: {errBody}");
                }

                var resultJson = JObject.Parse(await resultResp.Content.ReadAsStringAsync());
                return (resultJson, jobId, null);
            }
            catch (Exception ex)
            {
                return (null, null, ex.Message);
            }
        }
    }
}
