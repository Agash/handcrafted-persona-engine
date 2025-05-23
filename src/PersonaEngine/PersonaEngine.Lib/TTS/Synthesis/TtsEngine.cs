﻿using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PersonaEngine.Lib.Configuration;

namespace PersonaEngine.Lib.TTS.Synthesis;

/// <summary>
///     Main TTS engine implementation
/// </summary>
public class TtsEngine : ITtsEngine
{
    private readonly IList<IAudioFilter> _audioFilters;

    private readonly ITtsCache _cache;

    private readonly ILogger<TtsEngine> _logger;

    private readonly IOptionsMonitor<KokoroVoiceOptions> _options;

    private readonly IPhonemizer _phonemizer;

    private readonly IAudioSynthesizer _synthesizer;

    private readonly ITextProcessor _textProcessor;

    private readonly SemaphoreSlim _throttle;

    private bool _disposed;

    public TtsEngine(
        ITextProcessor                      textProcessor,
        IPhonemizer                         phonemizer,
        IAudioSynthesizer                   synthesizer,
        ITtsCache                           cache,
        IOptionsMonitor<KokoroVoiceOptions> options,
        IEnumerable<IAudioFilter>           audioFilters,
        ILoggerFactory                      loggerFactory)
    {
        _textProcessor = textProcessor ?? throw new ArgumentNullException(nameof(textProcessor));
        _phonemizer    = phonemizer ?? throw new ArgumentNullException(nameof(phonemizer));
        _synthesizer   = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _cache         = cache ?? throw new ArgumentNullException(nameof(cache));
        _options       = options;
        _audioFilters  = audioFilters.OrderByDescending(x => x.Priority).ToList();
        _logger        = loggerFactory?.CreateLogger<TtsEngine>() ?? throw new ArgumentNullException(nameof(loggerFactory));

        _throttle = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
    }

    public void Dispose() { _throttle.Dispose(); }

    /// <summary>
    ///     Synthesizes speech from a stream of text
    /// </summary>
    public async IAsyncEnumerable<AudioSegment> SynthesizeStreamingAsync(
        IAsyncEnumerable<string>                   textStream,
        KokoroVoiceOptions?                        options           = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = SynthesizeStreamingInternalAsync(textStream, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while ( true )
            {
                bool moveNextSuccess;

                try
                {
                    moveNextSuccess = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Streaming speech synthesis was canceled");

                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during streaming speech synthesis");

                    throw;
                }

                if ( !moveNextSuccess )
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private async IAsyncEnumerable<AudioSegment> SynthesizeStreamingInternalAsync(
        IAsyncEnumerable<string>                   textStream,
        KokoroVoiceOptions?                        options           = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var textBuffer = new StringBuilder(4096);

        await foreach ( var textChunk in textStream.WithCancellation(cancellationToken) )
        {
            if ( string.IsNullOrEmpty(textChunk) )
            {
                continue;
            }

            // Append to buffer
            textBuffer.Append(textChunk);
            var currentText = textBuffer.ToString();

            // Segment sentences from the buffer
            var processedText = await _textProcessor.ProcessAsync(currentText, cancellationToken);
            var sentences     = processedText.Sentences;

            // Skip if no complete sentences
            if ( sentences.Count <= 1 )
            {
                continue;
            }

            // Process all sentences except the last one (which might be incomplete)
            for ( var i = 0; i < sentences.Count - 1; i++ )
            {
                var sentence = sentences[i].Trim();
                if ( string.IsNullOrWhiteSpace(sentence) )
                {
                    continue;
                }

                // Process sentence and yield audio
                await foreach ( var segment in ProcessSentenceAsync(sentence, options, cancellationToken) )
                {
                    ApplyAudioFilters(segment);

                    yield return segment;
                }
            }

            // Clear the buffer and only keep the potentially incomplete last sentence
            textBuffer.Clear();
            textBuffer.Append(sentences[^1]);
        }

        // Process any remaining text
        var remainingText = textBuffer.ToString().Trim();
        if ( !string.IsNullOrEmpty(remainingText) )
        {
            await foreach ( var segment in ProcessSentenceAsync(remainingText, options, cancellationToken) )
            {
                ApplyAudioFilters(segment);

                yield return segment;
            }
        }
    }

    /// <summary>
    ///     Processes a single sentence for synthesis
    /// </summary>
    private async IAsyncEnumerable<AudioSegment> ProcessSentenceAsync(
        string                                     sentence,
        KokoroVoiceOptions?                        options           = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if ( string.IsNullOrWhiteSpace(sentence) )
        {
            yield break;
        }

        await _throttle.WaitAsync(cancellationToken);

        var currentOptions = options ?? _options.CurrentValue;

        try
        {
            // Get phonemes
            var phonemeResult = await _phonemizer.ToPhonemesAsync(sentence, cancellationToken);

            // Process each phoneme chunk
            foreach ( var phonemeChunk in SplitPhonemes(phonemeResult.Phonemes, 510) )
            {
                // Get audio
                var audioData = await _synthesizer.SynthesizeAsync(
                                                                   phonemeChunk,
                                                                   currentOptions,
                                                                   cancellationToken);

                // Apply timing
                ApplyTokenTimings(
                                  phonemeResult.Tokens,
                                  currentOptions,
                                  audioData.PhonemeTimings);

                // Return segment
                yield return new AudioSegment(
                                              audioData.Samples,
                                              options?.SampleRate ?? _options.CurrentValue.SampleRate,
                                              phonemeResult.Tokens);
            }
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>
    ///     Splits phonemes into manageable chunks
    /// </summary>
    private IEnumerable<string> SplitPhonemes(string phonemes, int maxLength)
    {
        if ( string.IsNullOrEmpty(phonemes) )
        {
            yield return string.Empty;

            yield break;
        }

        if ( phonemes.Length <= maxLength )
        {
            yield return phonemes;

            yield break;
        }

        var currentIndex = 0;
        while ( currentIndex < phonemes.Length )
        {
            var remainingLength = phonemes.Length - currentIndex;
            var chunkSize       = Math.Min(maxLength, remainingLength);

            // Find a good breakpoint (whitespace or punctuation)
            if ( chunkSize < remainingLength && chunkSize > 10 )
            {
                // Look backwards from the end to find a good breakpoint
                for ( var i = currentIndex + chunkSize - 1; i > currentIndex + 10; i-- )
                {
                    if ( char.IsWhiteSpace(phonemes[i]) || IsPunctuation(phonemes[i]) )
                    {
                        chunkSize = i - currentIndex + 1;

                        break;
                    }
                }
            }

            yield return phonemes.Substring(currentIndex, chunkSize);
            currentIndex += chunkSize;
        }
    }

    private bool IsPunctuation(char c) { return ".,:;!?-—()[]{}\"'".Contains(c); }

    private void ApplyAudioFilters(AudioSegment audioSegment)
    {
        foreach ( var audioFilter in _audioFilters )
        {
            audioFilter.Process(audioSegment);
        }
    }

    /// <summary>
    ///     Applies timing information to tokens
    /// </summary>
    private void ApplyTokenTimings(
        IReadOnlyList<Token> tokens,
        KokoroVoiceOptions   options,
        ReadOnlyMemory<long> phonemeTimings)
    {
        // Skip if no timing information
        if ( tokens.Count == 0 || phonemeTimings.Length < 3 )
        {
            return;
        }

        var timingsSpan = phonemeTimings.Span;

        // Magic scaling factor for timing conversion
        const int TIME_DIVISOR = 80;

        // Start with boundary tokens (often <bos> token)
        var leftTime  = options.TrimSilence ? 0 : 2 * Math.Max(0, timingsSpan[0] - 3);
        var rightTime = leftTime;

        // Process each token
        var timingIndex = 1;
        foreach ( var token in tokens )
        {
            // Skip tokens without phonemes
            if ( string.IsNullOrEmpty(token.Phonemes) )
            {
                // Handle whitespace timing specially
                if ( token.Whitespace == " " && timingIndex + 1 < timingsSpan.Length )
                {
                    timingIndex++;
                    leftTime  = rightTime + timingsSpan[timingIndex];
                    rightTime = leftTime + timingsSpan[timingIndex];
                    timingIndex++;
                }

                continue;
            }

            // Calculate end index for this token's phonemes
            var endIndex = timingIndex + (token.Phonemes?.Length ?? 0);
            if ( endIndex >= phonemeTimings.Length )
            {
                continue;
            }

            // Start time for this token
            var startTime = (double)leftTime / TIME_DIVISOR;

            // Sum durations for all phonemes in this token
            var tokenDuration = 0L;
            for ( var i = timingIndex; i < endIndex && i < timingsSpan.Length; i++ )
            {
                tokenDuration += timingsSpan[i];
            }

            // Handle whitespace after token
            var spaceDuration = token.Whitespace == " " && endIndex < timingsSpan.Length
                                    ? timingsSpan[endIndex]
                                    : 0;

            // Calculate end time
            leftTime = rightTime + 2 * tokenDuration + spaceDuration;
            var endTime = (double)leftTime / TIME_DIVISOR;
            rightTime = leftTime + spaceDuration;

            // Add token with timing
            token.StartTs = startTime;
            token.EndTs   = endTime;

            // Move to next token's timing
            timingIndex = endIndex + (token.Whitespace == " " ? 1 : 0);
        }
    }
}