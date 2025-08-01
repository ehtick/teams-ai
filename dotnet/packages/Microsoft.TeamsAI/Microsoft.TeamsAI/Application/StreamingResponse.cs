﻿using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Teams.AI.AI.Action;
using Microsoft.Teams.AI.AI.Models;
using Microsoft.Teams.AI.Exceptions;
using Microsoft.Teams.AI.Utilities;
using Newtonsoft.Json.Linq;
using System;

namespace Microsoft.Teams.AI.Application
{

    /// <summary>
    /// A helper class for streaming responses to the client.
    /// This class is used to send a series of updates to the client in a single response. The expected
    /// sequence of calls is:
    /// 
    /// `QueueInformativeUpdate()`, `QueueTextChunk()`, `QueueTextChunk()`, ..., `EndStream()`.
    ///
    ///  Once `EndStream()` is called, the stream is considered ended and no further updates can be sent.
    /// </summary>
    public class StreamingResponse
    {
        private readonly ITurnContext _context;
        private int _nextSequence = 1;
        private bool _ended = false;

        // Queue for outgoing activities
        private List<Func<Activity>> _queue = new();
        private Task? _queueSync;
        private bool _chunkQueued = false;

        /// <summary>
        /// Fluent interface for accessing the attachments.
        /// </summary>
        public List<Attachment>? Attachments { get; set; } = new();

        /// <summary>
        /// Sets the Feedback Loop in Teams that allows a user to give thumbs up or down to a response.
        /// Defaults to false.
        /// </summary>
        public bool? EnableFeedbackLoop { get; set; } = false;

        /// <summary>
        /// Represents the type of feedback loop. Set to "default" by default. It can be set to one of "default" or "custom".
        /// </summary>
        public string FeedbackLoopType { get; set; } = "default";

        /// <summary>
        /// Sets the "Generated by AI" label in Teams.
        /// Defaults to false.
        /// </summary>
        public bool? EnableGeneratedByAILabel { get; set; } = false;

        /// <summary>
        /// The citations for the response.
        /// </summary>
        public List<ClientCitation>? Citations { get; set; } = new();

        /// <summary>
        /// The sensitivity label for the response.
        /// </summary>
        public SensitivityUsageInfo? SensitivityLabel { get; set; }

        /// <summary>
        /// Gets the stream ID of the current response.
        /// Assigned after the initial update is sent.
        /// </summary>
        public string? StreamId { get; private set; }

        /// <summary>
        /// Fluent interface for accessing the message.
        /// </summary>
        public string Message { get; private set; } = "";

        /// <summary>
        /// Gets the number of updates sent for the stream.
        /// </summary>
        /// <returns>Number of updates sent so far.</returns>
        public int UpdatesSent() => this._nextSequence - 1;

        /// <summary>
        /// Creates a new instance of the <see cref="StreamingResponse"/> class.
        /// </summary>
        /// <param name="context">Context for the current turn of conversation with the user.</param>
        public StreamingResponse(ITurnContext context)
        {
            this._context = context;
        }

        /// <summary>
        /// Waits for the outgoing activity queue to be empty.
        /// </summary>
        /// <returns></returns>
        public Task WaitForQueue()
        {
            return this._queueSync != null ? this._queueSync : Task.CompletedTask;
        }

        /// <summary>
        ///  Sets the citations for the full message.
        /// </summary>
        /// <param name="citations">Citations to be included in the message.</param>
        public void SetCitations(IList<Citation> citations)
        {
            if (citations.Count > 0)
            {
                if (this.Citations == null)
                {
                    this.Citations = new List<ClientCitation>();
                }

                int currPos = this.Citations.Count;

                foreach (Citation citation in citations)
                {
                    string abs = CitationUtils.Snippet(citation.Content, 480);

                    this.Citations.Add(new ClientCitation()
                    {
                        Position = currPos + 1,
                        Appearance = new ClientCitationAppearance()
                        {
                            Name = citation.Title,
                            Abstract = abs,
                            Url = citation.Url
                        }
                    });
                    currPos++;
                }

            }
        }

        /// <summary>
        /// Queues an informative update to be sent to the client.
        /// </summary>
        /// <param name="text">Text of the update to send.</param>
        /// <exception cref="TeamsAIException">Throws if the stream has already ended.</exception>
        public void QueueInformativeUpdate(string text)
        {
            if (this._ended)
            {
                throw new TeamsAIException("The stream has already ended.");
            }

            QueueActivity(() => new Activity
            {
                Type = ActivityTypes.Typing,
                Text = text,
                ChannelData = new StreamingChannelData
                {
                    StreamType = StreamType.Informative,
                    StreamSequence = this._nextSequence++,
                }
            });
        }

        /// <summary>
        /// Queues a chunk of partial message text to be sent to the client.
        /// </summary>
        /// <param name="text">Partial text of the message to send.</param>
        /// <param name="citations">Citations to include in the message.</param>
        /// <exception cref="TeamsAIException">Throws if the stream has already ended.</exception>
        public void QueueTextChunk(string text, IList<Citation>? citations = null)
        {
            if (this._ended)
            {
                throw new TeamsAIException("The stream has already ended.");
            }

            Message += text;

            // If there are citations, modify the content so that the sources are numbers instead of [doc1], [doc2], etc.
            this.Message = CitationUtils.FormatCitationsResponse(this.Message);

            QueueNextChunk();
        }

        /// <summary>
        /// Ends the stream by sending the final message to the client.
        /// </summary>
        /// <returns>A Task representing the async operation</returns>
        /// <exception cref="TeamsAIException">Throws if the stream has already ended.</exception>
        public Task EndStream()
        {
            if (this._ended)
            {
                throw new TeamsAIException("The stream has already ended.");
            }

            this._ended = true;
            QueueNextChunk();

            // Wait for the queue to drain
            return WaitForQueue()!;
        }

        /// <summary>
        /// Queue an activity to be sent to the client.
        /// </summary>
        /// <param name="factory"></param>
        private void QueueActivity(Func<Activity> factory)
        {
            this._queue.Add(factory);

            // If there's no sync in progress, start one
            if (this._queueSync == null || this._queueSync.IsCompleted)
            {
                this._queueSync = DrainQueue();

                if (this._queueSync.IsFaulted)
                {
                    Exception ex = this._queueSync.Exception;
                    this._queueSync = null;
                    throw new TeamsAIException($"Error occurred when sending activity while streaming", ex);
                }
            }
        }

        /// <summary>
        /// Queue the next chunk of text to be sent to the client.
        /// </summary>
        private void QueueNextChunk()
        {
            // Check if we are already waiting to send a chunk
            if (this._chunkQueued)
            {
                return;
            }

            // Queue a chunk of text to be sent
            this._chunkQueued = true;
            QueueActivity(() =>
            {
                this._chunkQueued = false;

                if (this._ended)
                {
                    // Send final message
                    Activity activity = new Activity
                    {
                        Type = ActivityTypes.Message,
                        Text = Message,
                        ChannelData = new StreamingChannelData
                        {
                            StreamType = StreamType.Final,
                        }
                    };

                    if (Attachments != null && Attachments.Count > 0)
                    {
                        activity.Attachments = Attachments;
                    }
                    return activity;
                }
                else
                {
                    // Send typing activity
                    return new Activity
                    {
                        Type = ActivityTypes.Typing,
                        Text = Message,
                        ChannelData = new StreamingChannelData
                        {
                            StreamType = StreamType.Streaming,
                            StreamSequence = this._nextSequence++,
                        }
                    };

                }
            });
        }

        /// <summary>
        /// Sends any queued activities to the client until the queue is empty.
        /// </summary>
        private async Task DrainQueue()
        {
            try
            {
                while (this._queue.Count > 0)
                {
                    // Get next activity from queue
                    Activity activity = _queue[0]();
                    await SendActivity(activity).ConfigureAwait(false);
                    _queue.RemoveAt(0);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Sends an activity to the client and saves the stream ID returned.
        /// </summary>
        /// <param name="activity">The activity to send.</param>
        /// <returns>A Task representing the async operation.</returns>
        private async Task SendActivity(Activity activity)
        {
            // Set activity ID to the assigned stream ID
            if (!string.IsNullOrEmpty(StreamId))
            {
                StreamingChannelData oldChannelData = activity.GetChannelData<StreamingChannelData>();
                StreamingChannelData updatedChannelData = new()
                {
                    streamId = StreamId,
                    StreamType = oldChannelData.StreamType,
                };

                if (oldChannelData.StreamSequence != null)
                {
                    updatedChannelData.StreamSequence = oldChannelData.StreamSequence;
                }

                activity.ChannelData = updatedChannelData;
            }

            activity.Entities = new List<Entity>{
                new Entity("streaminfo")
                {
                    Properties = JObject.FromObject(new {
                        streamId = ((StreamingChannelData) activity.ChannelData).streamId,
                        streamType = ((StreamingChannelData) activity.ChannelData).StreamType.ToString(),
                        streamSequence = ((StreamingChannelData) activity.ChannelData).StreamSequence,

                    })
                }
            };

            if (this.Citations != null && this.Citations.Count > 0 && !this._ended)
            {
                // If there are citations, filter out the citations unused in content.
                List<ClientCitation>? currCitations = CitationUtils.GetUsedCitations(this.Message, this.Citations);
                AIEntity entity = new AIEntity();
                if (currCitations != null && currCitations.Count > 0)
                {
                    entity.Citation = currCitations;
                }

                activity.Entities.Add(entity);
            }

            // Add in Powered by AI feature flags
            if (this._ended)
            {
                // Add in feedback loop
                StreamingChannelData currChannelData = activity.GetChannelData<StreamingChannelData>();
                
                if (EnableFeedbackLoop == true)
                {
                    currChannelData.feedbackLoopEnabled = true;
                    currChannelData.feedbackLoopType = FeedbackLoopType;
                } 
                else
                {
                    currChannelData.feedbackLoopEnabled = false;
                }
                activity.ChannelData = currChannelData;

                // Add in Generated by AI
                if (this.EnableGeneratedByAILabel == true)
                {
                    AIEntity entity = new AIEntity();
                    if (this.Citations != null && this.Citations.Count > 0)
                    {
                        List<ClientCitation>? currCitations = CitationUtils.GetUsedCitations(this.Message, this.Citations);
                        if (currCitations != null && currCitations.Count > 0)
                        {
                            entity.Citation = currCitations;
                        }
                    }

                    entity.UsageInfo = this.SensitivityLabel;
                    activity.Entities.Add(entity);
                }
            }

            ResourceResponse response = await this._context.SendActivityAsync(activity).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // Save assigned stream ID
            if (string.IsNullOrEmpty(StreamId))
            {
                StreamId = response.Id;
            }
        }
    }
}
