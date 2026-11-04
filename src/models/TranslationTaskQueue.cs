// Modified by jtex3797 (2026-11): expose the translated text together with its original. Derived from SakiRinn/LiveCaptions-Translator (Apache-2.0).
namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> tasks;

        private (string translatedText, bool isChoke) output;
        public (string translatedText, bool isChoke) Output => output;

        // A reference-type snapshot, so that readers always see a consistent (translation, original) pair.
        private sealed record OutputSnapshot(string TranslatedText, bool IsChoke, string Original);
        private volatile OutputSnapshot? outputSnapshot;
        public (string translatedText, bool isChoke, string original) OutputWithOriginal
        {
            get
            {
                var snapshot = outputSnapshot;
                return snapshot == null ?
                    (string.Empty, false, string.Empty) :
                    (snapshot.TranslatedText, snapshot.IsChoke, snapshot.Original);
            }
        }

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            output = (string.Empty, false);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());
            lock (_lock)
            {
                tasks.Add(newTranslationTask);
            }
            // Run `OnTaskCompleted` in a new thread.
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            lock (_lock)
            {
                var index = tasks.IndexOf(translationTask);
                for (int i = 0; i < index; i++)
                    tasks[i].CTS.Cancel();
                for (int i = index; i >= 0; i--)
                    tasks.RemoveAt(i);
            }
            
            output = translationTask.Task.Result;
            outputSnapshot = new OutputSnapshot(output.translatedText, output.isChoke, translationTask.OriginalText);
            var translatedText = output.Item1;
            
            // Log after translation.
            bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
            if (!isOverwrite)
                await Translator.AddContexts();
            await Translator.Log(translationTask.OriginalText, translatedText, isOverwrite);
        }
    }

    public class TranslationTask
    {
        public Task<(string, bool)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
        }
    }
}