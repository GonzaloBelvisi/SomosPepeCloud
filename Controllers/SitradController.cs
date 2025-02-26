using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using SitradWebInterface.Models; // Asegúrate de tener Models/CameraViewModel.cs

namespace SitradWebInterface.Controllers
{
    public class SitradController : Controller
    {
        private readonly HttpClient _httpClient;

        public SitradController()
        {
            // Omitir validación de certificado (solo en desarrollo)
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler);

            // Autenticación básica
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("gonzalo:Almar1975"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        // Muestra la vista inicial
        public async Task<IActionResult> Index()
        {
            var data = await GetDashboardDataInternal();
            return View(data);
        }

        // Endpoint para actualización vía AJAX (retorna JSON)
        [HttpGet]
        public async Task<IActionResult> GetDashboardData()
        {
            var data = await GetDashboardDataInternal();
            return Json(data);
        }

        // Método interno que obtiene, agrupa y procesa los datos de la API
        private async Task<List<CameraViewModel>> GetDashboardDataInternal()
        {
            // URL para obtener la lista de instrumentos (cámaras)
            string instrumentsUrl = "https://localhost:8002/api/v1/instruments";
            HttpResponseMessage instrumentsResponse = await _httpClient.GetAsync(instrumentsUrl);
            if (!instrumentsResponse.IsSuccessStatusCode)
            {
                return new List<CameraViewModel>();
            }

            string instrumentsJson = await instrumentsResponse.Content.ReadAsStringAsync();
            var instrumentsContainer = JsonConvert.DeserializeObject<InstrumentResponse>(instrumentsJson);
            var instrumentList = instrumentsContainer.results;

            // Agrupar instrumentos por número de cámara
            // Usaremos un diccionario cuya clave es el número (por ejemplo, "4", "5", etc.) y el valor una tupla:
            // (instrumento principal, instrumento evaporador)
            var cameraGroups = new Dictionary<string, (InstrumentModel main, InstrumentModel evaporator)>();

            foreach (var instrument in instrumentList)
            {
                // Si el nombre empieza por "CAM S/EV", es el instrumento del evaporador
                if (instrument.name.StartsWith("CAM S/EV", StringComparison.OrdinalIgnoreCase))
                {
                    // Se asume que el número es la última parte del nombre, por ejemplo "CAM S/EV 4"
                    var parts = instrument.name.Split(' ');
                    string key = parts[parts.Length - 1].Trim();
                    if (!cameraGroups.ContainsKey(key))
                    {
                        cameraGroups[key] = (null, null);
                    }
                    cameraGroups[key] = (cameraGroups[key].main, instrument);
                }
                // Si el nombre empieza por "Camara" o "Cámara", es el instrumento principal
                else if (instrument.name.StartsWith("Camara", StringComparison.OrdinalIgnoreCase) ||
                         instrument.name.StartsWith("Cámara", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = instrument.name.Split(' ');
                    string key = parts[parts.Length - 1].Trim();
                    if (!cameraGroups.ContainsKey(key))
                    {
                        cameraGroups[key] = (null, null);
                    }
                    cameraGroups[key] = (instrument, cameraGroups[key].evaporator);
                }
            }

            var cameraViewModels = new List<CameraViewModel>();

            // Para cada grupo (cada cámara) se consultan los valores de los instrumentos
            foreach (var kvp in cameraGroups)
            {
                string key = kvp.Key;
                InstrumentModel mainInstrument = kvp.Value.main;
                InstrumentModel evaporatorInstrument = kvp.Value.evaporator;

                double? mainTemperature = null;
                double? humidity = null;
                double? evaporatorTemperature = null;

                if (mainInstrument != null)
                {
                    string valuesUrl = $"https://localhost:8002/api/v1/instruments/{mainInstrument.id}/values";
                    HttpResponseMessage valuesResponse = await _httpClient.GetAsync(valuesUrl);
                    if (valuesResponse.IsSuccessStatusCode)
                    {
                        string valuesJson = await valuesResponse.Content.ReadAsStringAsync();
                        var valuesContainer = JsonConvert.DeserializeObject<InstrumentValuesResponse>(valuesJson);
                        foreach (var group in valuesContainer.results)
                        {
                            if (group.code.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                            {
                                if (group.values != null && group.values.Count > 0 &&
                                    Double.TryParse(group.values[0].value.ToString(), out double temp))
                                {
                                    mainTemperature = temp;
                                }
                            }
                            else if (group.code.Equals("Humidity", StringComparison.OrdinalIgnoreCase))
                            {
                                if (group.values != null && group.values.Count > 0 &&
                                    Double.TryParse(group.values[0].value.ToString(), out double hum))
                                {
                                    humidity = hum;
                                }
                            }
                        }
                    }
                }

                if (evaporatorInstrument != null)
                {
                    string valuesUrl = $"https://localhost:8002/api/v1/instruments/{evaporatorInstrument.id}/values";
                    HttpResponseMessage valuesResponse = await _httpClient.GetAsync(valuesUrl);
                    if (valuesResponse.IsSuccessStatusCode)
                    {
                        string valuesJson = await valuesResponse.Content.ReadAsStringAsync();
                        var valuesContainer = JsonConvert.DeserializeObject<InstrumentValuesResponse>(valuesJson);
                        foreach (var group in valuesContainer.results)
                        {
                            if (group.code.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                            {
                                if (group.values != null && group.values.Count > 0 &&
                                    Double.TryParse(group.values[0].value.ToString(), out double temp))
                                {
                                    evaporatorTemperature = temp;
                                }
                            }
                        }
                    }
                }

                // Para el nombre de la cámara, se prefiere el instrumento principal; si no existe, se usa el evaporador; si ninguno, se usa la clave.
                string cameraName = mainInstrument != null ? mainInstrument.name : (evaporatorInstrument != null ? evaporatorInstrument.name : key);

                // Se arma el ViewModel; en la columna de "Evaporador" se asigna el valor obtenido del instrumento "CAM S/EV"
                var camViewModel = new CameraViewModel
                {
                    CameraName = cameraName,
                    Temperature = mainTemperature.HasValue ? mainTemperature.Value.ToString("N1") + "°C" : "N/A",
                    Humidity = humidity.HasValue ? humidity.Value.ToString("N1") + "%" : "N/A",
                    // Aquí se usará la propiedad "PulpTemperature" para el valor de Evaporador
                    PulpTemperature = evaporatorTemperature.HasValue ? evaporatorTemperature.Value.ToString("N1") + "°C" : "N/A"
                };

                cameraViewModels.Add(camViewModel);
            }

            return cameraViewModels;
        }

        // Clases para deserializar la respuesta de la API de instrumentos
        public class InstrumentResponse
        {
            public int resultsQty { get; set; }
            public List<InstrumentModel> results { get; set; }
            public int status { get; set; }
        }

        public class InstrumentModel
        {
            public int id { get; set; }
            public int converterId { get; set; }
            public string name { get; set; }
            public int address { get; set; }
            public int statusId { get; set; }
            public string status { get; set; }
            public int modelId { get; set; }
            public int modelVersion { get; set; }
            public bool isAlarmsManuallyInhibited { get; set; }
        }

        // Clases para deserializar la respuesta de valores de un instrumento
        public class InstrumentValuesResponse
        {
            public int resultsQty { get; set; }
            public List<InstrumentValueGroup> results { get; set; }
            public int status { get; set; }
        }

        public class InstrumentValueGroup
        {
            public string code { get; set; }
            public string name { get; set; }
            public List<InstrumentValue> values { get; set; }
        }

        public class InstrumentValue
        {
            public string date { get; set; }
            public object value { get; set; }
            public int decimalPlaces { get; set; }
            public bool isInError { get; set; }
            public bool isEnabled { get; set; }
            public bool isFailPayload { get; set; }
            public int? measurementUnityId { get; set; }
            public string measurementUnity { get; set; }
        }
    }
}
