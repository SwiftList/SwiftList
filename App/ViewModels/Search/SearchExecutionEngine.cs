using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SwiftList.Core;
using SwiftList.App.Services;
namespace SwiftList.App.ViewModels.Search
{
    internal sealed class SearchExecutionEngine : IDisposable
    {
        private readonly SearchService _searchService;
        private CancellationTokenSource? _searchCts;
        private readonly object _searchCtsLock = new();
        private CancellationTokenSource? _debounceCts;
        private int _searchVersion;

        public SearchExecutionEngine(SearchService searchService)
        {
            _searchService = searchService;
        }

        public void QueueSearch(

            string query,
            string? searchScope,
            bool isInlineSearchContext,
            Action<bool> onSearchStateChanged,
            Action<List<AppSearchResult>, string, bool> onResultsUpdated,
            Action onServiceUnavailable)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;

            _ = Task.Delay(35, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>

                    PerformSearch(query, searchScope, isInlineSearchContext, onSearchStateChanged, onResultsUpdated, onServiceUnavailable));
            }, cts.Token);
        }

        public void PerformSearch(

            string query,
            string? searchScope,
            bool isInlineSearchContext,
            Action<bool> onSearchStateChanged,
            Action<List<AppSearchResult>, string, bool> onResultsUpdated,
            Action onServiceUnavailable)
        {
            CancelPendingSearch();
            if (string.IsNullOrWhiteSpace(query))
            {
                onSearchStateChanged(false);
                onResultsUpdated(new List<AppSearchResult>(), string.Empty, true);
                return;
            }

            onSearchStateChanged(true);
            var cts = new CancellationTokenSource();
            int searchVersion = Interlocked.Increment(ref _searchVersion);

            lock (_searchCtsLock)
            {
                _searchCts = cts;
            }

            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var tracker = InlineSearchManager.Instance.ExplorerTracker;
                    var adapter = tracker.ActiveInlineAdapter;
                    if (isInlineSearchContext && adapter != null && tracker.ActiveHwnd != IntPtr.Zero)
                    {
                        var listItems = adapter.GetListItems(tracker.ActiveHwnd);
                        if (listItems.Any())
                        {
                            string? contextDirectory = !string.IsNullOrWhiteSpace(searchScope)
                                ? searchScope
                                : (tracker.ActivePath ?? tracker.LastActiveExplorerPath);

                            if (tracker.IsActiveWindowExplorer)
                            {
                                var localMatches = InlineListSearchHelper.GetLocalMatches(query, listItems, contextDirectory, token);
                                await PerformStreamingSearchAsync(query, null, contextDirectory, isInlineSearchContext, searchVersion, onResultsUpdated, onServiceUnavailable, token, localMatches);
                                return;
                            }
                            else
                            {
                                InlineListSearchHelper.PerformInlineListProviderSearch(query, adapter, tracker.ActiveHwnd, listItems, contextDirectory, searchVersion, () => Volatile.Read(ref _searchVersion), onResultsUpdated, token);
                                return;
                            }
                        }
                    }

                    // Fall through to streaming search when the adapter provides no list items

                    // (e.g. desktop, or adapters that only implement ExecuteItem).

                    // Only restrict scope to the current directory when an actual Explorer window is active;

                    // for all other contexts (desktop, dialog, etc.) scope must be null so global search runs.

                    string? streamingScope = tracker.IsActiveWindowExplorer ? searchScope : null;

                    string? streamingContextDirectory = isInlineSearchContext

                        ? (!string.IsNullOrWhiteSpace(searchScope) ? searchScope : tracker.ActivePath ?? tracker.LastActiveExplorerPath)

                        : tracker.LastActiveExplorerPath;
                    await PerformStreamingSearchAsync(query, streamingScope, streamingContextDirectory, isInlineSearchContext, searchVersion, onResultsUpdated, onServiceUnavailable, token);
                }

                catch (OperationCanceledException) { }

                catch (Exception ex)
                {
                    Logger.Log($"[SearchExecutionEngine] PerformSearch failed: {ex}", SwiftList.Core.LogLevel.Error);
                }

                finally
                {
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lock (_searchCtsLock)
                        {
                            if (_searchCts == cts)
                            {
                                onSearchStateChanged(false);
                            }
                        }

                    }));
                }

            }, token);
        }

        private async Task PerformStreamingSearchAsync(
            string query,
            string? searchScope,
            string? contextDirectory,
            bool isInlineSearchContext,
            int searchVersion,
            Action<List<AppSearchResult>, string, bool> onResultsUpdated,
            Action onServiceUnavailable,
            CancellationToken token,
            List<AppSearchResult>? localMatches = null)
        {
            var streamedResponse = new SearchResponse();
            object responseLock = new();
            int streamedCount = 0;
            int hasRenderedFirstBatch = 0;

            void RenderSnapshot(bool final)
            {
                void ApplySnapshot()
                {
                    if (searchVersion != Volatile.Read(ref _searchVersion) || token.IsCancellationRequested)
                        return;
                    SearchResponse snapshot;

                    lock (responseLock)
                    {
                        snapshot = new SearchResponse
                        {
                            AppResults = new List<SearchResult>(streamedResponse.AppResults),
                            FileResults = new List<SearchResult>(streamedResponse.FileResults)
                        };
                    }

                    var uiResults = SearchResultMapper.BuildQuickResults(snapshot, query, searchScope, contextDirectory, isInlineSearchContext);

                    if (localMatches != null && localMatches.Count > 0)
                    {
                        var combinedResults = new List<AppSearchResult>();

                        if (uiResults.Count > 0)
                        {
                            SearchResultMapper.AddSectionHeader(combinedResults, TranslationManager.Instance["Search_LocalFolderHeader"] ?? "Current Folder", query);
                        }

                        foreach (var match in localMatches)
                        {
                            combinedResults.Add(match);
                        }

                        if (uiResults.Count > 0)
                        {
                            SearchResultMapper.AddSectionHeader(combinedResults, TranslationManager.Instance["Search_GlobalSearchHeader"] ?? "Global Search", query);
                            foreach (var res in uiResults)
                            {
                                combinedResults.Add(res);
                            }
                        }

                        for (int idx = 0; idx < combinedResults.Count; idx++)
                        {
                            combinedResults[idx].Index = idx;
                        }
                        uiResults = combinedResults;
                    }

                    if (final && uiResults.Count == 0)
                        uiResults.Add(SearchResultMapper.CreateNoResultsResult(query));
                    string statusText = "";
                    if (uiResults.Count > 0)
                        statusText = SearchResultMapper.FormatSearchStatus(snapshot.AppResults.Count, snapshot.FileResults.Count);
                    else if (final)
                        statusText = "No matching results";
                    onResultsUpdated(uiResults, statusText, final);
                }

                if (final)
                    System.Windows.Application.Current.Dispatcher.Invoke(new Action(ApplySnapshot));
                else
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(ApplySnapshot));
            }

            bool ok = await _searchService.SearchStreamingAsync(query, 51, 51, searchScope, (result, isApplication) =>
            {
                token.ThrowIfCancellationRequested();

                lock (responseLock)
                {
                    if (isApplication)
                        streamedResponse.AppResults.Add(result);
                    else
                        streamedResponse.FileResults.Add(result);
                    streamedCount++;
                }

                if (Volatile.Read(ref hasRenderedFirstBatch) == 0 && Volatile.Read(ref streamedCount) < 9)
                    return;
                if (Interlocked.CompareExchange(ref hasRenderedFirstBatch, 1, 0) == 0)
                {
                    RenderSnapshot(final: false);
                }

            }, token);
            if (!ok)
            {
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        onServiceUnavailable();
                    }

                }));
                return;
            }

            token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref hasRenderedFirstBatch, 1);
            RenderSnapshot(final: true);
        }

        public void CancelPendingSearch()
        {
            lock (_searchCtsLock)
            {
                if (_searchCts != null)
                {
                    _searchCts.Cancel();
                    _searchCts.Dispose();
                    _searchCts = null;
                }
            }
        }

        public void Dispose()
        {
            CancelPendingSearch();
            _debounceCts?.Dispose();
        }
    }
}
