using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Intent;
using AliceNeural.Utils;
using AliceNeural.Models;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Web;
using HttpProxyControl;
using System.Text.Json.Serialization;
using System.Globalization;
using WikiText;
namespace AliceNeural
{
    public class BingMapsStore
    {
        [JsonPropertyName("api_key")]
        public string APIKeyValue { get; set; } = string.Empty;

    }
    public partial class MainPage : ContentPage
    {

        static readonly HttpClient _client = HttpProxyHelper.CreateHttpClient(setProxy: true);
        //static readonly BingMapsStore bingMapsStore = GetDataFromStore();
        //static readonly string bingMapsAPIKey = bingMapsStore.APIKeyValue;
        static readonly string bingMapsAPIKey = "AtfW_jPNhXKclBn7qqG71JOJqcLmkvXrGi1Zp6QW4QUW7CUM8JhwEPrCWrRPM2cF";
        SpeechRecognizer? speechRecognizer;
        IntentRecognizer? intentRecognizerByPatternMatching;
        IntentRecognizer? intentRecognizerByCLU;
        SpeechSynthesizer? speechSynthesizer;
        TaskCompletionSourceManager<int>? taskCompletionSourceManager;
        AzureCognitiveServicesResourceManager? serviceManager;
        bool buttonToggle = false;
        Brush? buttonToggleColor;
        private static readonly JsonSerializerOptions? jsonSerializationOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        public MainPage()
        {
            InitializeComponent();
            serviceManager = new AzureCognitiveServicesResourceManager("MyResponder", "firstDeploy");
            taskCompletionSourceManager = new TaskCompletionSourceManager<int>();
            (intentRecognizerByPatternMatching, speechSynthesizer, intentRecognizerByCLU) =
                ConfigureContinuousIntentPatternMatchingWithMicrophoneAsync(
                    serviceManager.CurrentSpeechConfig,
                    serviceManager.CurrentCluModel,
                    serviceManager.CurrentPatternMatchingModel,
                    taskCompletionSourceManager);
            speechRecognizer = new SpeechRecognizer(serviceManager.CurrentSpeechConfig);
        }
        protected override async void OnDisappearing()
        {
            base.OnDisappearing();

            if (speechSynthesizer != null)
            {
                await speechSynthesizer.StopSpeakingAsync();
                speechSynthesizer.Dispose();
            }

            if (intentRecognizerByPatternMatching != null)
            {
                await intentRecognizerByPatternMatching.StopContinuousRecognitionAsync();
                intentRecognizerByPatternMatching.Dispose();
            }

            if (intentRecognizerByCLU != null)
            {
                await intentRecognizerByCLU.StopContinuousRecognitionAsync();
                intentRecognizerByCLU.Dispose();
            }
        }

        private async void ContentPage_Loaded(object sender, EventArgs e)
        {
            await CheckAndRequestMicrophonePermission();
        }

        private async Task<PermissionStatus> CheckAndRequestMicrophonePermission()
        {
            PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status == PermissionStatus.Granted)
            {
                return status;
            }
            if (Permissions.ShouldShowRationale<Permissions.Microphone>())
            {
                // Prompt the user with additional information as to why the permission is needed
                await DisplayAlert("Permission required", "Microphone permission is necessary", "OK");
            }
            status = await Permissions.RequestAsync<Permissions.Microphone>();
            return status;
        }

        private static async Task ContinuousIntentPatternMatchingWithMicrophoneAsync(
            IntentRecognizer intentRecognizer, TaskCompletionSourceManager<int> stopRecognition)
        {
            await intentRecognizer.StartContinuousRecognitionAsync();
            // Waits for completion. Use Task.WaitAny to keep the task rooted.
            Task.WaitAny(new[] { stopRecognition.TaskCompletionSource.Task });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="config"></param>
        /// <param name="cluModel"></param>
        /// <param name="patternMatchingModelCollection"></param>
        /// <param name="stopRecognitionManager"></param>
        /// <returns>una tupla contentente nell'ordine un intent recognizer basato su Patter Matching, un sintetizzatore vocale e un intent recognizer basato su un modello di Conversational Language Understanding </returns>
        private (IntentRecognizer, SpeechSynthesizer, IntentRecognizer) ConfigureContinuousIntentPatternMatchingWithMicrophoneAsync(
            SpeechConfig config,
            ConversationalLanguageUnderstandingModel cluModel,
            LanguageUnderstandingModelCollection patternMatchingModelCollection,
            TaskCompletionSourceManager<int> stopRecognitionManager)
        {
            //creazione di un intent recognizer basato su pattern matching
            var intentRecognizerByPatternMatching = new IntentRecognizer(config);
            intentRecognizerByPatternMatching.ApplyLanguageModels(patternMatchingModelCollection);

            //creazione di un intent recognizer basato su CLU
            var intentRecognizerByCLU = new IntentRecognizer(config);
            var modelsCollection = new LanguageUnderstandingModelCollection { cluModel };
            intentRecognizerByCLU.ApplyLanguageModels(modelsCollection);

            //creazione di un sitetizzatore vocale
            var synthesizer = new SpeechSynthesizer(config);

            //gestione eventi
            intentRecognizerByPatternMatching.Recognized += async (s, e) =>
            {
                switch (e.Result.Reason)
                {
                    case ResultReason.RecognizedSpeech:
                        Debug.WriteLine($"PATTERN MATCHING - RECOGNIZED SPEECH: Text= {e.Result.Text}");
                        break;
                    case ResultReason.RecognizedIntent:
                        {
                            Debug.WriteLine($"PATTERN MATCHING - RECOGNIZED INTENT: Text= {e.Result.Text}");
                            Debug.WriteLine($"       Intent Id= {e.Result.IntentId}.");
                            if (e.Result.IntentId == "Ok")
                            {
                                Debug.WriteLine("Stopping current speaking if any...");
                                await synthesizer.StopSpeakingAsync();
                                Debug.WriteLine("Stopping current intent recognition by CLU if any...");
                                await intentRecognizerByCLU.StopContinuousRecognitionAsync();
                                await HandleOkCommand(synthesizer, intentRecognizerByCLU).ConfigureAwait(false);
                            }
                            else if (e.Result.IntentId == "Stop")
                            {
                                Debug.WriteLine("Stopping current speaking...");
                                await synthesizer.StopSpeakingAsync();
                            }
                        }

                        break;
                    case ResultReason.NoMatch:
                        Debug.WriteLine($"NOMATCH: Speech could not be recognized.");
                        var noMatch = NoMatchDetails.FromResult(e.Result);
                        switch (noMatch.Reason)
                        {
                            case NoMatchReason.NotRecognized:
                                Debug.WriteLine($"PATTERN MATCHING - NOMATCH: Speech was detected, but not recognized.");
                                break;
                            case NoMatchReason.InitialSilenceTimeout:
                                Debug.WriteLine($"PATTERN MATCHING - NOMATCH: The start of the audio stream contains only silence, and the service timed out waiting for speech.");
                                break;
                            case NoMatchReason.InitialBabbleTimeout:
                                Debug.WriteLine($"PATTERN MATCHING - NOMATCH: The start of the audio stream contains only noise, and the service timed out waiting for speech.");
                                break;
                            case NoMatchReason.KeywordNotRecognized:
                                Debug.WriteLine($"PATTERN MATCHING - NOMATCH: Keyword not recognized");
                                break;
                        }
                        break;

                    default:
                        break;
                }
            };
            intentRecognizerByPatternMatching.Canceled += (s, e) =>
            {
                Debug.WriteLine($"PATTERN MATCHING - CANCELED: Reason={e.Reason}");

                if (e.Reason == CancellationReason.Error)
                {
                    Debug.WriteLine($"PATTERN MATCHING - CANCELED: ErrorCode={e.ErrorCode}");
                    Debug.WriteLine($"PATTERN MATCHING - CANCELED: ErrorDetails={e.ErrorDetails}");
                    Debug.WriteLine($"PATTERN MATCHING - CANCELED: Did you update the speech key and location/region info?");
                }
                stopRecognitionManager.TaskCompletionSource.TrySetResult(0);
            };
            intentRecognizerByPatternMatching.SessionStopped += (s, e) =>
            {
                Debug.WriteLine("\n    Session stopped event.");
                stopRecognitionManager.TaskCompletionSource.TrySetResult(0);
            };

            return (intentRecognizerByPatternMatching, synthesizer, intentRecognizerByCLU);

        }
        public async Task HandleOkCommand(SpeechSynthesizer synthesizer, IntentRecognizer intentRecognizer)
        {
            MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = "");
            await synthesizer.SpeakTextAsync("Sono in ascolto");
            //avvia l'intent recognition su Azure
            string? jsonResult = await RecognizeIntentAsync(intentRecognizer);
            if (jsonResult != null)
            {
                //process jsonResult
                //deserializzo il json
                CLUResponse cluResponse = JsonSerializer.Deserialize<CLUResponse>(jsonResult, jsonSerializationOptions) ?? new CLUResponse();
                await synthesizer.SpeakTextAsync($"La tua richiesta è stata {cluResponse.Result?.Query}");
                var topIntent = cluResponse.Result?.Prediction?.TopIntent;
                if (topIntent != null)
                {
                    switch (topIntent)
                    {
                        case string intent when intent.Contains("Wiki"):
                            await synthesizer.SpeakTextAsync("Vuoi fare una ricerca su Wikipedia");
                            string mainSearch = string.Empty;
                            string subSearch = "";
                            for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                            {
                                if (cluResponse.Result.Prediction.Entities[i].Category == "WikiSearch.MainSearch")
                                {
                                    mainSearch = cluResponse.Result.Prediction.Entities[i].Text;
                                }
                                else if (cluResponse.Result.Prediction.Entities[i].Category == "WikiSearch.SubItemSearch")
                                {
                                    subSearch = cluResponse.Result.Prediction.Entities[i].Text;
                                }
                            }
                            await Wikipedia(synthesizer, mainSearch, subSearch);
                            break;
                        case string intent when intent.Contains("Weather"):
                            if (cluResponse.Result?.Query.Contains("umidità") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere come è l'umidità");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "oggi";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await Umidità(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("velocità") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere come è la velocità del vento");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "oggi";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await VelocitàVento(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("direzione") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere come è la direzione del vento");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "oggi";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await DirezioneVento(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("temperatura") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere come è la temperatura");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "oggi";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await Temperatura(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("tempo") == true || cluResponse.Result?.Query.Contains("previsioni") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere come è il tempo");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "oggi";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await PrevisioniMeteo(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("piove") == true || cluResponse.Result?.Query.Contains("pioggia") == true || cluResponse.Result?.Query.Contains("Pioverà") == true || cluResponse.Result?.Query.Contains("piovere") == true || cluResponse.Result?.Query.Contains("Piove") == true || cluResponse.Result?.Query.Contains("pioverà") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere quando pioverà");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await PrevisioniPioggia(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("nevica") == true || cluResponse.Result?.Query.Contains("neve") == true || cluResponse.Result?.Query.Contains("Nevicherà") == true || cluResponse.Result?.Query.Contains("nevicherà") == true || cluResponse.Result?.Query.Contains("nevicare") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere quando nevicherà");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await PrevisioniNeve(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("sole") == true || cluResponse.Result?.Query.Contains("soleggiato") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere quando ci sarà il sole");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await PrevisioniSole(synthesizer, city, date);
                            }
                            if (cluResponse.Result?.Query.Contains("nuvole") == true || cluResponse.Result?.Query.Contains("nuvoloso") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere quando sarà nuvoloso");
                                string date = string.Empty, city = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (cluResponse.Result.Prediction.Entities[i].Category == "datetimeV2")
                                    {
                                        date = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (date == string.Empty)
                                {
                                    date = "";
                                }
                                if (city == string.Empty)
                                {
                                    city = "Monticello Brianza";
                                }
                                await PrevisioniNuvole(synthesizer, city, date);
                            }
                            break;
                        case string intent when intent.Contains("Places"):
                            if (cluResponse.Result?.Query.Contains("distante") == true || cluResponse.Result?.Query.Contains("Distanza") == true || cluResponse.Result?.Query.Contains("distanza") == true || cluResponse.Result?.Query.Contains("dista") == true || cluResponse.Result?.Query.Contains("Lontananza") == true || cluResponse.Result?.Query.Contains("lontananza") == true)
                            {
                                await synthesizer.SpeakTextAsync("Vuoi sapere qual è la distanza");
                                string city1 = string.Empty, city2 = string.Empty;
                                for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                {
                                    if (city1 == string.Empty && cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                    {
                                        city1 = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (city1 == string.Empty && cluResponse.Result.Prediction.Entities[i].Category == "Places.PlaceName")
                                    {
                                        city1 = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                    else if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                    {
                                        city2 = cluResponse.Result.Prediction.Entities[i].Text;
                                    }
                                }
                                if (city2 == string.Empty)
                                {
                                    city2 = "Monticello Brianza";
                                }
                                await RouteWp1ToWp2(synthesizer, city1, city2);
                            }
                            else
                            {
                                string zona = string.Empty, poi = string.Empty;
                                if (cluResponse.Result?.Query.Contains("bar") == true || cluResponse.Result?.Query.Contains("caffetteria") == true || cluResponse.Result?.Query.Contains("Caffetteria") == true || cluResponse.Result?.Query.Contains("Bar") == true || cluResponse.Result?.Query.Contains("caffè") == true || cluResponse.Result?.Query.Contains("Caffè") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali bar ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Bars";
                                }
                                if (cluResponse.Result?.Query.Contains("ristorante") == true || cluResponse.Result?.Query.Contains("Ristorante") == true || cluResponse.Result?.Query.Contains("Ristoranti") == true || cluResponse.Result?.Query.Contains("ristoranti") == true || cluResponse.Result?.Query.Contains("cibo") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali ristoranti ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Restaurants";
                                }
                                if (cluResponse.Result?.Query.Contains("ospedale") == true || cluResponse.Result?.Query.Contains("Ospedale") == true || cluResponse.Result?.Query.Contains("Ospedali") == true || cluResponse.Result?.Query.Contains("ospedali") == true || cluResponse.Result?.Query.Contains("Pronto soccorso") == true || cluResponse.Result?.Query.Contains("pronto soccorso") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali ospedali ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Hospitals";
                                }
                                if (cluResponse.Result?.Query.Contains("hotel") == true || cluResponse.Result?.Query.Contains("Hotel") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali hotel ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "HotelsAndMotels";
                                }
                                if (cluResponse.Result?.Query.Contains("parco") == true || cluResponse.Result?.Query.Contains("Parco") == true || cluResponse.Result?.Query.Contains("Parchi") == true || cluResponse.Result?.Query.Contains("parchi") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali parchi ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Parks";
                                }
                                if (cluResponse.Result?.Query.Contains("libreria") == true || cluResponse.Result?.Query.Contains("Libreria") == true || cluResponse.Result?.Query.Contains("librerie") == true || cluResponse.Result?.Query.Contains("Librerie") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali librerie ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Bookstores";
                                }
                                if (cluResponse.Result?.Query.Contains("scuola") == true || cluResponse.Result?.Query.Contains("Scuola") == true || cluResponse.Result?.Query.Contains("scuole") == true || cluResponse.Result?.Query.Contains("Scuole") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali scuole ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Education";
                                }
                                if (cluResponse.Result?.Query.Contains("chiesa") == true || cluResponse.Result?.Query.Contains("Chiesa") == true || cluResponse.Result?.Query.Contains("chiese") == true || cluResponse.Result?.Query.Contains("Chiese") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali chiese ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Religion";
                                }
                                if (cluResponse.Result?.Query.Contains("parrucchiere") == true || cluResponse.Result?.Query.Contains("Parrucchiere") == true || cluResponse.Result?.Query.Contains("parrucchieri") == true || cluResponse.Result?.Query.Contains("Parrucchieri") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali parrucchieri ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Barbers";
                                }
                                if (cluResponse.Result?.Query.Contains("negozio") == true || cluResponse.Result?.Query.Contains("Negozio") == true || cluResponse.Result?.Query.Contains("negozi") == true || cluResponse.Result?.Query.Contains("Negozi") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali negozi ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Shop";
                                }
                                if (cluResponse.Result?.Query.Contains("museo") == true || cluResponse.Result?.Query.Contains("Museo") == true || cluResponse.Result?.Query.Contains("musei") == true || cluResponse.Result?.Query.Contains("Musei") == true)
                                {
                                    await synthesizer.SpeakTextAsync("Vuoi sapere quali musei ci sono");
                                    for (int i = 0; i < cluResponse.Result.Prediction.Entities.Count; i++)
                                    {
                                        if (cluResponse.Result.Prediction.Entities[i].Category == "Places.AbsoluteLocation")
                                        {
                                            zona = cluResponse.Result.Prediction.Entities[i].Text;
                                        }
                                    }
                                    if (zona == string.Empty)
                                    {
                                        zona = "";
                                    }
                                    poi = "Museums";
                                }
                                await FindPointOfInterest(synthesizer, zona, poi);
                            }
                            break;
                        case string intent when intent.Contains("None"):
                            await synthesizer.SpeakTextAsync("Non ho capito");
                            break;
                    }

                }
                //determino l'action da fare, eventualmente effettuando una richiesta GET su un endpoint remoto scelto in base al topScoringIntent
                //ottengo il risultato dall'endpoit remoto
                //effettuo un text to speech per descrivere il risultato
            }
            else
            {
                //è stato restituito null - ad esempio quando il processo è interrotto prima di ottenre la risposta dal server
                Debug.WriteLine("Non è stato restituito nulla dall'intent reconition sul server");
            }
        }

        public static async Task<string?> RecognizeIntentAsync(IntentRecognizer recognizer)
        {
            // Starts recognizing.
            Debug.WriteLine("Say something...");

            // Starts intent recognition, and returns after a single utterance is recognized. The end of a
            // single utterance is determined by listening for silence at the end or until a maximum of 15
            // seconds of audio is processed.  The task returns the recognition text as result. 
            // Note: Since RecognizeOnceAsync() returns only a single utterance, it is suitable only for single
            // shot recognition like command or query. 
            // For long-running multi-utterance recognition, use StartContinuousRecognitionAsync() instead.
            var result = await recognizer.RecognizeOnceAsync();
            string? languageUnderstandingJSON = null;

            // Checks result.
            switch (result.Reason)
            {
                case ResultReason.RecognizedIntent:
                    Debug.WriteLine($"RECOGNIZED: Text={result.Text}");
                    Debug.WriteLine($"    Intent Id: {result.IntentId}.");
                    languageUnderstandingJSON = result.Properties.GetProperty(PropertyId.LanguageUnderstandingServiceResponse_JsonResult);
                    Debug.WriteLine($"    Language Understanding JSON: {languageUnderstandingJSON}.");
                    CLUResponse cluResponse = JsonSerializer.Deserialize<CLUResponse>(languageUnderstandingJSON, jsonSerializationOptions) ?? new CLUResponse();
                    Debug.WriteLine("Risultato deserializzato:");
                    Debug.WriteLine($"kind: {cluResponse.Kind}");
                    Debug.WriteLine($"result.query: {cluResponse.Result?.Query}");
                    Debug.WriteLine($"result.prediction.topIntent: {cluResponse.Result?.Prediction?.TopIntent}");
                    Debug.WriteLine($"result.prediction.Intents[0].Category: {cluResponse.Result?.Prediction?.Intents?[0].Category}");
                    Debug.WriteLine($"result.prediction.Intents[0].ConfidenceScore: {cluResponse.Result?.Prediction?.Intents?[0].ConfidenceScore}");
                    Debug.WriteLine($"result.prediction.entities: ");
                    cluResponse.Result?.Prediction?.Entities?.ForEach(s => Debug.WriteLine($"\tcategory = {s.Category}; text= {s.Text};"));
                    break;
                case ResultReason.RecognizedSpeech:
                    Debug.WriteLine($"RECOGNIZED: Text={result.Text}");
                    Debug.WriteLine($"    Intent not recognized.");
                    break;
                case ResultReason.NoMatch:
                    Debug.WriteLine($"NOMATCH: Speech could not be recognized.");
                    break;
                case ResultReason.Canceled:
                    var cancellation = CancellationDetails.FromResult(result);
                    Debug.WriteLine($"CANCELED: Reason={cancellation.Reason}");

                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Debug.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Debug.WriteLine($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
                        Debug.WriteLine($"CANCELED: Did you update the subscription info?");
                    }
                    break;
            }
            return languageUnderstandingJSON;
        }
        private async void OnRecognitionButtonClicked2(object sender, EventArgs e)
        {
            if (serviceManager != null && taskCompletionSourceManager != null)
            {
                buttonToggle = !buttonToggle;
                if (buttonToggle)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        buttonToggleColor = RecognizeSpeechBtn.Background;
                    });

                    RecognizeSpeechBtn.Background = Colors.Yellow;
                    //creo le risorse
                    //su un dispositivo mobile potrebbe succedere che cambiando rete cambino i parametri della rete, ed in particolare il proxy
                    //In questo caso, per evitare controlli troppo complessi, si è scelto di ricreare lo speechConfig ad ogni richiesta se cambia il proxy
                    if (serviceManager.ShouldRecreateSpeechConfigForProxyChange())
                    {
                        (intentRecognizerByPatternMatching, speechSynthesizer, intentRecognizerByCLU) =
                       ConfigureContinuousIntentPatternMatchingWithMicrophoneAsync(
                           serviceManager.CurrentSpeechConfig,
                           serviceManager.CurrentCluModel,
                           serviceManager.CurrentPatternMatchingModel,
                           taskCompletionSourceManager);
                    }

                    _ = Task.Factory.StartNew(async () =>
                    {
                        taskCompletionSourceManager.TaskCompletionSource = new TaskCompletionSource<int>();
                        await ContinuousIntentPatternMatchingWithMicrophoneAsync(
                            intentRecognizerByPatternMatching!, taskCompletionSourceManager)
                        .ConfigureAwait(false);
                    });
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        RecognizeSpeechBtn.Background = buttonToggleColor;
                    });
                    //la doppia chiamata di StopSpeakingAsync è un work-around a un problema riscontrato in alcune situazioni:
                    //se si prova a fermare il task mentre il sintetizzatore sta parlando, in alcuni casi si verifica un'eccezione. 
                    //Con il doppio StopSpeakingAsync non succede.
                    await speechSynthesizer!.StopSpeakingAsync();
                    await speechSynthesizer.StopSpeakingAsync();
                    await intentRecognizerByCLU!.StopContinuousRecognitionAsync();
                    await intentRecognizerByPatternMatching!.StopContinuousRecognitionAsync();
                    //speechSynthesizer.Dispose();
                    //intentRecognizerByPatternMatching.Dispose();
                }
            }
        }
        private async void OnRecognitionButtonClicked(object sender, EventArgs e)
        {
            try
            {
                //accedo ai servizi
                //AzureCognitiveServicesResourceManager serviceManager =(Application.Current as App).AzureCognitiveServicesResourceManager;
                // Creates a speech recognizer using microphone as audio input.
                // Starts speech recognition, and returns after a single utterance is recognized. The end of a
                // single utterance is determined by listening for silence at the end or until a maximum of 15
                // seconds of audio is processed.  The task returns the recognition text as result.
                // Note: Since RecognizeOnceAsync() returns only a single utterance, it is suitable only for single
                // shot recognition like command or query.
                // For long-running multi-utterance recognition, use StartContinuousRecognitionAsync() instead.
                var result = await speechRecognizer!.RecognizeOnceAsync().ConfigureAwait(false);

                // Checks result.
                StringBuilder sb = new();
                if (result.Reason == ResultReason.RecognizedSpeech)
                {
                    sb.AppendLine($"RECOGNIZED: Text={result.Text}");
                    await speechSynthesizer!.SpeakTextAsync(result.Text);
                }
                else if (result.Reason == ResultReason.NoMatch)
                {
                    sb.AppendLine($"NOMATCH: Speech could not be recognized.");
                }
                else if (result.Reason == ResultReason.Canceled)
                {
                    var cancellation = CancellationDetails.FromResult(result);
                    sb.AppendLine($"CANCELED: Reason={cancellation.Reason}");

                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        sb.AppendLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        sb.AppendLine($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
                        sb.AppendLine($"CANCELED: Did you update the subscription info?");
                    }
                }
                UpdateUI(sb.ToString());
            }
            catch (Exception ex)
            {
                UpdateUI("Exception: " + ex.ToString());
            }
        }
        private void UpdateUI(String message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RecognitionText.Text = message;
            });
        }
        public static async Task<(double? lat, double? lon)?> GetCoordinate(string? città, string language = "it", int count = 1)
        {
            string? cittaCod = HttpUtility.UrlEncode(città);
            string urlCoordinate = $"https://geocoding-api.open-meteo.com/v1/search?name={cittaCod}&count={count}&language={language}";
            try
            {
                HttpResponseMessage response = await _client.GetAsync($"{urlCoordinate}");
                if (response.IsSuccessStatusCode)
                {
                    await Console.Out.WriteLineAsync(await response.Content.ReadAsStringAsync());
                    Geocoding? geoCoding = await response.Content.ReadFromJsonAsync<Geocoding>();
                    if (geoCoding != null && geoCoding.Results?.Count > 0)
                    {
                        return (geoCoding.Results[0].Latitude, geoCoding.Results[0].Longitude);
                    }
                }
                return null;
            }
            catch (Exception)
            {

                Console.WriteLine("Errore");
            }
            return null;
        }
        public async Task PrevisioniMeteo(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    await synthesizer.SpeakTextAsync($"\nPrevisioni meteo per {città} oggi");
                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"\nPrevisioni meteo per {città} oggi \nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nT max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;\nT min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C;\nprevisione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                    await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                    $" T max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;" +
                    $" T min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C; " +
                    $"previsione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nPrevisioni meteo per {città} domani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"\nPrevisioni meteo per {città} domani \nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nT max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;\nT min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C;\nprevisione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" T max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;" +
                        $" T min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C; " +
                        $"previsione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nPrevisioni meteo per {città} dopodomani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"\nPrevisioni meteo per {città} dopodomani \nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nT max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;\nT min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C;\nprevisione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" T max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;" +
                        $" T min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C; " +
                        $"previsione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nPrevisioni meteo per {città} {data}");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"\nPrevisioni meteo per {città} {data} \nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nT max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;\nT min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C;\nprevisione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" T max = {UtilsClass.Display(forecast.Daily?.Temperature2mMax?[0], datoNonFornitoString)} °C;" +
                        $" T min = {UtilsClass.Display(forecast.Daily?.Temperature2mMin?[0], datoNonFornitoString)} °C; " +
                        $"previsione = {UtilsClass.Display(UtilsClass.WMOCodesIntIT(forecast.Daily?.WeatherCode?[0]), datoNonFornitoString)}");
                    }
                }


            }
        }
        public async Task Umidità(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    await synthesizer.SpeakTextAsync($"\nUmidità per {città} oggi");
                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Umidità per {città} oggi\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)}; \numidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                    await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                    $" umidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nUmidità per {città} domani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Umidità per {città} domani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)}; \numidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" umidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nUmidità per {città} dopodomani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Umidità per {città} dopodomani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)}; \numidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" umidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nUmidità per {città} {data}");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Umidità per {città} {data}\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)}; \numidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" umidità: {UtilsClass.Display(forecast.Hourly?.RelativeHumidity2m[0], datoNonFornitoString)}");
                    }
                }


            }
        }
        public async Task VelocitàVento(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    await synthesizer.SpeakTextAsync($"\nVelocità del vento per {città} oggi");
                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Velocità del vento per {città} oggi\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nVelocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                    await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                    $"Velocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nVelocità del vento per {città} domani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Velocità del vento per {città} domani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nVelocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $"Velocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nVelocità del vento per {città} dopodomani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Velocità del vento per {città} dopodomani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nVelocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $"Velocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nVelocità del vento per {città} {data}");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Velocità del vento per {città} {data}\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nVelocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $"Velocità del vento: {UtilsClass.Display(forecast.Current.WindSpeed10m, datoNonFornitoString)} Km/h");
                    }
                }


            }
        }
        public async Task Temperatura(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    await synthesizer.SpeakTextAsync($"\nTemperatura per {città} oggi");
                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Temperatura per {città} oggi\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\ntemperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                    await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                    $" temperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nTemperatura per {città} domani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Temperatura per {città} domani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\ntemperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" temperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nTemperatura per {città} dopodomani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Temperatura per {città} dopodomani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\ntemperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" temperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nTemperatura per {città} {data}");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Temperatura per {città} {data}\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\ntemperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $" temperatura: {UtilsClass.Display(forecast.Current.Temperature2m, datoNonFornitoString)} °C;");
                    }
                }


            }
        }
        public async Task DirezioneVento(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    await synthesizer.SpeakTextAsync($"\nDirezione del vento per {città} oggi");
                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Direzione del vento per {città} oggi\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nDirezione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                    await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                    $"Direzione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nDirezione del vento per {città} domani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Direzione del vento per {città} domani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nDirezione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $"Direzione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nDirezione del vento per {città} dopodomani");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Direzione del vento per {città} dopodomani\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nDirezione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $"Direzione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        await synthesizer.SpeakTextAsync($"\nDirezione del vento per {città} {data}");
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Direzione del vento per {città} {data}\nData e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};\nDirezione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                        await synthesizer.SpeakTextAsync($"Data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Daily?.Time?[0]), datoNonFornitoString)};" +
                        $"Direzione del vento: {UtilsClass.Display(forecast.Current.WindDirection10m, datoNonFornitoString)} °");
                    }
                }


            }
        }
        public async Task PrevisioniPioggia(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    int count = 0;
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    if (forecast.Hourly != null)
                    {
                        int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                        if (numeroPrevisioni > 0)
                        {
                            for (int i = 0; i < numeroPrevisioni; i++)
                            {
                                if (forecast.Hourly.Rain[i] != 0)
                                {
                                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    await synthesizer.SpeakTextAsync($" Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    count++;
                                }
                            }
                        }
                    }
                    if (count == 0)
                    {
                        await synthesizer.SpeakTextAsync($"Oggi non pioverà");
                    }
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        int count = 0;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.Rain[i] != 0)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Domani non pioverà");
                        }
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        int count = 0;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.Rain[i] != 0)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Dopodomani non pioverà");
                        }
                    }
                    else if (data == "")
                    {
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        int count = 0;
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.Rain[i] != 0)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Questa settimana non pioverà");
                        }
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        int count = 0;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.Rain[i] != 0)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Pioverà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"{data} non pioverà");
                        }
                    }
                }


            }
        }
        public async Task PrevisioniNeve(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    int count = 0;
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    if (forecast.Hourly != null)
                    {
                        int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                        if (numeroPrevisioni > 0)
                        {
                            for (int i = 0; i < numeroPrevisioni; i++)
                            {
                                if (forecast.Hourly.WeatherCode[i] == 71 || forecast.Hourly.WeatherCode[i] == 73 || forecast.Hourly.WeatherCode[i] == 75 || forecast.Hourly.WeatherCode[i] == 77)
                                {
                                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    await synthesizer.SpeakTextAsync($" Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    count++;
                                }
                            }
                        }
                    }
                    if (count == 0)
                    {
                        await synthesizer.SpeakTextAsync($"Oggi non nevicherà");
                    }
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        int count = 0;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 71 || forecast.Hourly.WeatherCode[i] == 73 || forecast.Hourly.WeatherCode[i] == 75 || forecast.Hourly.WeatherCode[i] == 77)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Domani non nevicherà");
                        }
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        int count = 0;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 71 || forecast.Hourly.WeatherCode[i] == 73 || forecast.Hourly.WeatherCode[i] == 75 || forecast.Hourly.WeatherCode[i] == 77)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Dopodomani non nevicherà");
                        }
                    }
                    else if (data == "")
                    {
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        int count = 0;
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 71 || forecast.Hourly.WeatherCode[i] == 73 || forecast.Hourly.WeatherCode[i] == 75 || forecast.Hourly.WeatherCode[i] == 77)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Questa settimana non nevicherà");
                        }
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        int count = 0;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 71 || forecast.Hourly.WeatherCode[i] == 73 || forecast.Hourly.WeatherCode[i] == 75 || forecast.Hourly.WeatherCode[i] == 77)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($" Nevicherà in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"{data} non nevicherà");
                        }
                    }
                }


            }
        }
        public async Task PrevisioniNuvole(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    int count = 0;
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    if (forecast.Hourly != null)
                    {
                        int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                        if (numeroPrevisioni > 0)
                        {
                            for (int i = 0; i < numeroPrevisioni; i++)
                            {
                                if (forecast.Hourly.WeatherCode[i] == 2 || forecast.Hourly.WeatherCode[i] == 3)
                                {
                                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    await synthesizer.SpeakTextAsync($"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    count++;
                                }
                            }
                        }
                    }
                    if (count == 0)
                    {
                        await synthesizer.SpeakTextAsync($"Oggi non sarà nuvoloso");
                    }
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {

                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        int count = 0;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 2 || forecast.Hourly.WeatherCode[i] == 3)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Domani non sarà nuvoloso");
                        }
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        int count = 0;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 2 || forecast.Hourly.WeatherCode[i] == 3)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Dopodomani non sarà nuvoloso");
                        }
                    }
                    else if (data == "")
                    {
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        int count = 0;
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 2 || forecast.Hourly.WeatherCode[i] == 3)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Questa settimana non sarà nuvoloso");
                        }
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        int count = 0;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 2 || forecast.Hourly.WeatherCode[i] == 3)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Sarà nuvoloso in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"{data} non sarà nuvoloso");
                        }
                    }
                }


            }
        }
        public async Task PrevisioniSole(SpeechSynthesizer synthesizer, string città, string data)
        {
            const string datoNonFornitoString = "";
            var geo = await GetCoordinate(città);
            if (geo != null)
            {
                FormattableString addressUrlFormattable;
                string addressUrl;
                HttpResponseMessage response;
                if (data == "oggi")
                {
                    addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&forecast_days=1";
                    addressUrl = FormattableString.Invariant(addressUrlFormattable);
                    response = await _client.GetAsync($"{addressUrl}");
                    int count = 0;
                    OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                    if (forecast.Hourly != null)
                    {
                        int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                        if (numeroPrevisioni > 0)
                        {
                            for (int i = 0; i < numeroPrevisioni; i++)
                            {
                                if (forecast.Hourly.WeatherCode[i] == 0 || forecast.Hourly.WeatherCode[i] == 1)
                                {
                                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    await synthesizer.SpeakTextAsync($"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                    count++;
                                }
                            }
                        }
                    }
                    if (count == 0)
                    {
                        await synthesizer.SpeakTextAsync($"Oggi non ci sarà il sole");
                    }
                }
                if (data != "oggi")
                {
                    if (data == "domani")
                    {
                        DateTime domani = DateTime.Today.AddDays(1);
                        string dataDomani;
                        int count = 0;
                        if (domani.Month < 10)
                        {
                            dataDomani = $"{domani.Year}-0{domani.Month}-{domani.Day}";
                        }
                        else
                        {
                            dataDomani = $"{domani.Year}-{domani.Month}-{domani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDomani}&end_date={dataDomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 0 || forecast.Hourly.WeatherCode[i] == 1)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Domani non ci sarà il sole");
                        }
                    }
                    else if (data == "dopodomani")
                    {
                        DateTime dopodomani = DateTime.Today.AddDays(2);
                        string dataDopodomani;
                        int count = 0;
                        if (dopodomani.Month < 10)
                        {
                            dataDopodomani = $"{dopodomani.Year}-0{dopodomani.Month}-{dopodomani.Day}";
                        }
                        else
                        {
                            dataDopodomani = $"{dopodomani.Year}-{dopodomani.Month}-{dopodomani.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={dataDopodomani}&end_date={dataDopodomani}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 0 || forecast.Hourly.WeatherCode[i] == 1)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Dopodomani non ci sarà il sole");
                        }
                    }
                    else if (data == "")
                    {
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        int count = 0;
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 0 || forecast.Hourly.WeatherCode[i] == 1)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"Questa settimana non ci sarà il sole");
                        }
                    }
                    else
                    {
                        DateTime day = DateTime.Parse(data);
                        string date;
                        int count = 0;
                        if (day.Month < 10)
                        {
                            date = $"{DateTime.Today.Year}-0{day.Month}-{day.Day}";
                        }
                        else
                        {
                            date = $"{DateTime.Today.Year}-{day.Month}-{day.Day}";
                        }
                        addressUrlFormattable = $"https://api.open-meteo.com/v1/forecast?latitude={geo?.lat}&longitude={geo?.lon}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability,precipitation,rain,showers,weather_code,wind_speed_10m,wind_direction_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min&timeformat=unixtime&timezone=auto&start_date={date}&end_date={date}";
                        addressUrl = FormattableString.Invariant(addressUrlFormattable);
                        response = await _client.GetAsync($"{addressUrl}");
                        OpenMeteoForecast? forecast = await response.Content.ReadFromJsonAsync<OpenMeteoForecast>();
                        if (forecast.Hourly != null)
                        {
                            int? numeroPrevisioni = forecast.Hourly.Time?.Count;
                            if (numeroPrevisioni > 0)
                            {
                                for (int i = 0; i < numeroPrevisioni; i++)
                                {
                                    if (forecast.Hourly.WeatherCode[i] == 0 || forecast.Hourly.WeatherCode[i] == 1)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        await synthesizer.SpeakTextAsync($"Ci sarà il sole in data e ora = {UtilsClass.Display(UtilsClass.UnixTimeStampToDateTime(forecast.Hourly.Time?[i]), datoNonFornitoString)};");
                                        count++;
                                    }
                                }
                            }
                        }
                        if (count == 0)
                        {
                            await synthesizer.SpeakTextAsync($"{data} non ci sarà il sole");
                        }
                    }
                }


            }
        }

        //static BingMapsStore GetDataFromStore()
        //{
        //    string keyStorePath =

        //    string store = File.ReadAllText(keyStorePath);
        //    BingMapsStore? bingMapsStore = JsonSerializer.Deserialize<BingMapsStore>(store);
        //    return bingMapsStore ?? new BingMapsStore();
        //}
        async Task RouteWp1ToWp2(SpeechSynthesizer synthesizer, string wp1, string wp2)
        {
            string wp1Encode = HttpUtility.UrlEncode(wp1);
            string wp2Encode = HttpUtility.UrlEncode(wp2);
            string urlCompleto = $"https://dev.virtualearth.net/REST/v1/Routes?wp.1={wp1Encode}&wp.2={wp2Encode}&optimize=time&tt=departure&dt=2024-04-11%2019:35:00&distanceUnit=km&c=it&ra=regionTravelSummary&key={bingMapsAPIKey}";
            HttpResponseMessage response = await _client.GetAsync(urlCompleto);
            if (response.IsSuccessStatusCode)
            {
                LocalRoute? localRoute = await response.Content.ReadFromJsonAsync<LocalRoute>();
                if (localRoute != null)
                {
                    // distanza in km
                    double? distanza = localRoute.ResourceSets[0].Resources[0].TravelDistance;
                    double durata = localRoute.ResourceSets[0].Resources[0].TravelDuration;
                    double durataConTraffico = localRoute.ResourceSets[0].Resources[0].TravelDurationTraffic;
                    string modViaggio = localRoute.ResourceSets[0].Resources[0].TravelMode;
                    MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"La distanza da {wp1} a {wp2}  è di {distanza} KM" +
                        $"\ncon una durata di {Math.Truncate(durata / 60)} minuti e " +
                        $"con {Math.Truncate(durataConTraffico / 60)} minuti con traffico attuale utilizzando" +
                        $" la {modViaggio} ");
                    await synthesizer.SpeakTextAsync($"La distanza da {wp1} a {wp2}  è di {distanza} KM" +
                        $"\ncon una durata di {Math.Truncate(durata / 60)} minuti e " +
                        $"con {Math.Truncate(durataConTraffico / 60)} minuti con traffico attuale utilizzando" +
                        $" la {modViaggio} ");
                }
            }
        }
        async Task FindPointOfInterest(SpeechSynthesizer synthesizer, string città, string POI)
        {
            string stringa = string.Empty;
            double? lat = 0;
            double? lon = 0;
            if (città == "")
            {
                try
                {
                    Location location = await Geolocation.Default.GetLastKnownLocationAsync();

                    if (location != null)
                    {
                        lat = location.Latitude;
                        lon = location.Longitude;
                    }
                        
                }
                catch (FeatureNotSupportedException fnsEx)
                {
                    // Handle not supported on device exception
                }
                catch (FeatureNotEnabledException fneEx)
                {
                    // Handle not enabled on device exception
                }
                catch (PermissionException pEx)
                {
                    // Handle permission exception
                }
                catch (Exception ex)
                {
                    // Unable to get location
                }
            }
            else
            {
                var geo = await GetCoordinate(città);
                lat = geo?.lat;
                lon = geo?.lon;
            }
            if (lat != null)
            {
                FormattableString urlComplete = $"https://dev.virtualearth.net/REST/v1/LocationRecog/{lat},{lon}?radius=1&top=20&distanceunit=km&verboseplacenames=true&includeEntityTypes=businessAndPOI,naturalPOI,address&type={POI}&includeNeighborhood=1&include=ciso2&key={bingMapsAPIKey}";
                // converte le virgole in punti per la latituine e longitudine
                string addressUrl = FormattableString.Invariant(urlComplete);
                HttpResponseMessage response = await _client.GetAsync(addressUrl);
                if (response.IsSuccessStatusCode)
                {
                    LocalRecognition? data = await response.Content.ReadFromJsonAsync<LocalRecognition>();
                    int numeroPunti = data.ResourceSets[0].Resources[0].BusinessesAtLocation.Count;
                    var resources = data.ResourceSets[0].Resources[0];
                    if (numeroPunti==0)
                    {
                        await synthesizer.SpeakTextAsync("Nessun punto di interesse trovato");
                    }
                    else
                    {
                        for (int i = 0; i < numeroPunti; i++)
                        {
                            stringa = $"{stringa}\n\n{resources.BusinessesAtLocation[i].BusinessInfo.EntityName}";
                            if (resources.BusinessesAtLocation[i].BusinessInfo.Type != null)
                            {
                                stringa = $"{stringa}\n{resources.BusinessesAtLocation[i].BusinessInfo.Type}";
                            }
                            if (resources.BusinessesAtLocation[i].BusinessInfo.Phone != null)
                            {
                                stringa = $"{stringa}\n{resources.BusinessesAtLocation[i].BusinessInfo.Phone}";
                            }
                        }
                        MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"{stringa}");
                        await synthesizer.SpeakTextAsync(stringa);
                        await synthesizer.SpeakTextAsync("");
                    }
                }
            }
        }
        static async Task<string> SearchKeyText(string argument)
        {
            string argumentClean = HttpUtility.UrlEncode(argument);
            string wikiUrl = $"https://it.wikipedia.org/w/rest.php/v1/search/page?q={argumentClean}&limit=1";
            // recupero la chiave di ricerca con il parsing del dom
            var response = await _client.GetAsync(wikiUrl);
            //Console.WriteLine(wikiResult);
            if (response.IsSuccessStatusCode)
            {
                KeyModel? model = await response.Content.ReadFromJsonAsync<KeyModel>();
                if (model != null)
                {
                    string? keySearch = model.Pages[0].Key;

                    return keySearch;
                }
            }
            return null;
        }
        async Task Wikipedia(SpeechSynthesizer synthesizer, string mainSearch, string subSearch)
        {
            string? key = await SearchKeyText($"{mainSearch}");
            bool sezioneTrovata = false;
            string wikitext = string.Empty, readableText = string.Empty;
            if (subSearch != "")
            {
                TextInfo myTI = new CultureInfo("en-US", false).TextInfo;
                subSearch = myTI.ToTitleCase(subSearch);
                string urlSection = $"https://it.wikipedia.org/w/api.php?action=parse&format=json&page={key}&prop=sections&disabletoc=1";
                var response = await _client.GetAsync(urlSection);
                // parso le sezioni e recupero la key e l'indice di sezione
                if (response.IsSuccessStatusCode)
                {
                    SectionModel? sectionModel = await response.Content.ReadFromJsonAsync<SectionModel>();
                    if (sectionModel != null)
                    {
                        List<Section> sections = sectionModel.Parse.Sections;
                        foreach (Section section in sections)
                        {
                            if (section.Line == $"{subSearch}")
                            {
                                urlSection = $"https://it.wikipedia.org/w/api.php?action=parse&format=json&page={key}&prop=wikitext&section={section.Index}&disabletoc=1";
                                string wikiSummaryJSON = await _client.GetStringAsync(urlSection);
                                using JsonDocument document = JsonDocument.Parse(wikiSummaryJSON);
                                JsonElement root = document.RootElement;
                                JsonElement parse = root.GetProperty("parse");
                                JsonElement text = parse.GetProperty("wikitext");
                                if (text.TryGetProperty("*", out JsonElement extract))
                                {
                                    wikitext = extract.ToString();
                                    readableText = wikitext.WikiTextToReadableTextNoSpace();
                                    sezioneTrovata = true;
                                }
                                MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"{readableText}");
                                await synthesizer.SpeakTextAsync(readableText);
                            }
                        }
                        if (sezioneTrovata == false)
                        {
                            await synthesizer.SpeakTextAsync("Nessuna sezione trovata");
                            subSearch = "";
                        }
                    }
                }
            }
            if (subSearch == "")
            {
                string wikiUrl = $"https://it.wikipedia.org/w/api.php?format=json&action=query&prop=extracts&exintro&explaintext&exsectionformat=plain&redirects=1&titles={key}";
                string wikiSummaryJSON = await _client.GetStringAsync(wikiUrl);
                using JsonDocument document = JsonDocument.Parse(wikiSummaryJSON);
                JsonElement root = document.RootElement;
                JsonElement query = root.GetProperty("query");
                JsonElement pages = query.GetProperty("pages");
                //per prendere il primo elemento dentro pages, creo un enumeratore delle properties
                JsonElement.ObjectEnumerator enumerator = pages.EnumerateObject();
                //quando si crea un enumeratore su una collection, si deve farlo avanzare di una posizione per portarlo sul primo elemento della collection
                //il primo elemento corrisponde all'id della pagina all'interno dell'oggetto pages
                if (enumerator.MoveNext())
                {
                    //accedo all'elemento
                    JsonElement targetJsonElem = enumerator.Current.Value;
                    if (targetJsonElem.TryGetProperty("extract", out JsonElement extract))
                    {
                        wikitext = extract.ToString();
                        readableText = wikitext.WikiTextToReadableTextNoSpace();
                    }
                }
                MainThread.BeginInvokeOnMainThread(() => lblStampa.Text = $"{readableText}");
                await synthesizer.SpeakTextAsync(readableText);
            }
        }
    }
}
