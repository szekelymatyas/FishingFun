﻿using log4net;
using log4net.Appender;
using log4net.Repository.Hierarchy;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace FishingFun
{
    public class FishingBot
    {
        public static ILog logger = LogManager.GetLogger(typeof(FishingBot));

        private ConsoleKey castKey;
        private List<ConsoleKey> tenMinKey;
        private IBobberFinder bobberFinder;
        private IBiteWatcher biteWatcher;
        private Stopwatch stopwatch = new Stopwatch();
        private static Random random = new Random();

        public event EventHandler<FishingEvent>? FishingEventHandler;

        public FishingBot(IBobberFinder bobberFinder, IBiteWatcher biteWatcher, ConsoleKey castKey, List<ConsoleKey> tenMinKey)
        {
            this.bobberFinder = bobberFinder;
            this.biteWatcher = biteWatcher;
            this.castKey = castKey;
            this.tenMinKey = tenMinKey;

            logger.Info("FishBot Created.");
        }

        private CancellationTokenSource? cts;
        public async Task Start(CancellationToken ct)
        {
            biteWatcher.FishingEventHandler = (e) => FishingEventHandler?.Invoke(this, e);
            if (cts is not null && !cts.Token.IsCancellationRequested)
                cts.Cancel();

            var newCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts = newCts;
            await DoTenMinuteKey();

            
            while (!newCts.Token.IsCancellationRequested)
            {
                try
                {
                    logger.Info($"Pressing key {castKey} to Cast.");

                    await PressTenMinKeyIfDue();

                    if (newCts.Token.IsCancellationRequested) break;
                    FishingEventHandler?.Invoke(this, new FishingEvent { Action = FishingAction.Cast });
                    await WowProcess.PressKey(castKey);

                    if (newCts.Token.IsCancellationRequested) break;
                    await Watch(2000, newCts.Token);

                    if (newCts.Token.IsCancellationRequested) break;
                    await WaitForBite(newCts.Token);
                }
                catch (Exception e)
                {
                    logger.Error(e.ToString()); 
                    if (newCts.Token.IsCancellationRequested) break;

                    await Sleep(2000, newCts.Token);
                }
            }

            logger.Error("Bot has Stopped.");
        }

        public void SetCastKey(ConsoleKey castKey)
        {
            this.castKey = castKey;
        }

        private async Task Watch(int milliseconds, CancellationToken cancellationToken)
        {
            bobberFinder.Reset();
            stopwatch.Reset();
            stopwatch.Start();
            while (stopwatch.ElapsedMilliseconds < milliseconds && !cancellationToken.IsCancellationRequested)
            {
                await bobberFinder.FindAsync(cancellationToken);
            }
            stopwatch.Stop();
        }

        public void Stop()
        {
            if(cts is not null && !cts.Token.IsCancellationRequested)
            {
                cts.Cancel();
                cts = null;
                logger.Error("Bot is Stopping...");
            };
        }

        private async Task WaitForBite(CancellationToken cancellationToken)
        {
            bobberFinder.Reset();

            var bobberPosition = await FindBobber(cancellationToken);
            if (bobberPosition == Point.Empty || cancellationToken.IsCancellationRequested)
                return;

            this.biteWatcher.Reset(bobberPosition);

            logger.Info("Bobber start position: " + bobberPosition);

            var timedTask = new TimedAction((a) => { logger.Info("Fishing timed out!"); }, 25 * 1000, 25);

            // Wait for the bobber to move
            while (!cancellationToken.IsCancellationRequested)
            {
                var currentBobberPosition = await FindBobber(cancellationToken);
                if (currentBobberPosition == Point.Empty || currentBobberPosition.X == 0 || cancellationToken.IsCancellationRequested)
                    return;

                if (this.biteWatcher.IsBite(currentBobberPosition))
                {
                    await Loot(bobberPosition); 
                    await PressTenMinKeyIfDue();
                    return;
                }

                if (!timedTask.ExecuteIfDue()) { return; }
            }
        }

        private DateTime StartTime = DateTime.Now;

        private async Task PressTenMinKeyIfDue()
        {
            if ((DateTime.Now - StartTime).TotalMinutes > 10 && tenMinKey.Count > 0)
            {
                await DoTenMinuteKey();
            }
        }

        /// <summary>
        /// Ten minute key can do anything you want e.g.
        /// Macro to apply a lure: 
        /// /use Bright Baubles
        /// /use 16
        /// 
        /// Or a macro to delete junk:
        /// /run for b=0,4 do for s=1,GetContainerNumSlots(b) do local n=GetContainerItemLink(b,s) if n and (strfind(n,"Raw R") or strfind(n,"Raw Spot") or strfind(n,"Raw Glo") or strfind(n,"roup")) then PickupContainerItem(b,s) DeleteCursorItem() end end end
        /// </summary>
        private async Task DoTenMinuteKey()
        {
            StartTime = DateTime.Now;

            if (tenMinKey.Count == 0)
            {
                logger.Info($"Ten Minute Key:  No keys defined in tenMinKey, so nothing to do (Define in call to FishingBot constructor).");
            }

            FishingEventHandler?.Invoke(this, new FishingEvent { Action = FishingAction.Cast });

            foreach (var key in tenMinKey)
            {
                logger.Info($"Ten Minute Key: Pressing key {key} to run a macro, delete junk fish or apply a lure etc.");
                await WowProcess.PressKey(key);
            }
        }

        private async Task Loot(Point bobberPosition)
        {
            logger.Info($"Right clicking mouse to Loot.");
            await WowProcess.RightClickMouse(logger, bobberPosition);
        }

        public static async Task Sleep(int ms, CancellationToken cancellationToken)
        {
            ms+=random.Next(0, 225);

            Stopwatch sw = new Stopwatch();
            sw.Start();
            while (sw.Elapsed.TotalMilliseconds < ms && !cancellationToken.IsCancellationRequested)
            {
                FlushBuffers();
                await Task.Delay(100);
            }
        }

        public static void FlushBuffers()
        {
            ILog log = LogManager.GetLogger(typeof(FishingBot));
            var logger = log.Logger as Logger;
            if (logger != null)
            {
                foreach (IAppender appender in logger.Appenders)
                {
                    var buffered = appender as BufferingAppenderSkeleton;
                    if (buffered != null)
                    {
                        buffered.Flush();
                    }
                }
            }
        }

        private async Task<Point> FindBobber(CancellationToken cancellationToken)
        {
            var timer = new TimedAction((a) => { logger.Info("Waited seconds for target: " + a.ElapsedSecs); }, 1000, 5);

            while (!cancellationToken.IsCancellationRequested)
            {
                var target = await this.bobberFinder.FindAsync(cancellationToken);
                if (target != Point.Empty || !timer.ExecuteIfDue() ) { return target; }
            }
            return Point.Empty;
        }
    }
}