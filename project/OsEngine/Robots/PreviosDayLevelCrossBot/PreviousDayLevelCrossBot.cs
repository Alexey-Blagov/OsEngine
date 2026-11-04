/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;

/* Description

Alert robot. Watches High/Low of the previous finished candle and sends a Telegram
notification when the price reaches the target level.

Important: levels are taken from CandlesFinishedOnly by a 500 ms timer, so the robot
does NOT depend on CandleFinishedEvent and starts working immediately after start
(also on Day timeframe, where the candle closes only once a day).

Alert fires when the price is ALREADY beyond the trigger and no alert was sent for
this level yet. One alert per level per bar, no duplicates.

Mode Approach: trigHigh = High - offset, trigLow = Low + offset.
Mode Breakout: trigHigh = High + offset, trigLow = Low - offset.

Log levels: Verbose (all), ErrorsOnly (errors only), Silent (nothing).
Diag period logs bars/price/triggers every N seconds to show that the robot is alive.

Robot does not trade. Notifications only.
*/

namespace OsEngine.Robots.Custom
{
    [Bot("PreviousDayLevelCrossBot")]
    public class PreviousDayLevelCrossBot : BotPanel
    {
        private BotTabSimple _tab;

        // ---------- Parameters ----------
        private StrategyParameterString _regime;
        private StrategyParameterString _telegramToken;
        private StrategyParameterString _telegramChatId;
        private StrategyParameterInt _offsetTicks;
        private StrategyParameterString _mode;
        private StrategyParameterString _logLevel;
        private StrategyParameterInt _diagPeriodSec;

        // ---------- Levels ----------
        private readonly object _lock = new object();

        private decimal _prevHigh;
        private decimal _prevLow;
        private decimal _prevClose;
        private decimal _trigHigh;
        private decimal _trigLow;
        private decimal _offset;
        private DateTime _levelsBarTime = DateTime.MinValue;
        private string _periodLabel = "предыдущего бара";

        // ---------- Alert state (one alert per level per bar) ----------
        private bool _highAlerted;
        private bool _lowAlerted;

        // ---------- Last known price ----------
        private decimal _lastPrice;

        // ---------- Diagnostics ----------
        private DateTime _lastDiag = DateTime.MinValue;

        // ---------- Send queue ----------
        private readonly ConcurrentQueue<string> _sendQueue = new ConcurrentQueue<string>();
        private Thread _sendThread;
        private volatile bool _running = true;

        // ---------- Timer ----------
        private Timer _pollTimer;

        // ---------- HttpClient ----------
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        public PreviousDayLevelCrossBot(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            _tab = TabsSimple[0];

            _regime = CreateParameter("Regime", "On", new[] { "On", "Off" });
            _telegramToken = CreateParameter("Telegram Token", "");
            _telegramChatId = CreateParameter("Telegram Chat ID", "");
            _offsetTicks = CreateParameter("Offset (ticks)", 0, 0, 10000, 1);
            _mode = CreateParameter("Mode (Approach/Breakout)", "Approach", new[] { "Approach", "Breakout" });
            _logLevel = CreateParameter("LogLevel (Verbose/Signals/ErrorsOnly/Silent)", "Verbose",
                                        new[] { "Verbose", "Signals", "ErrorsOnly", "Silent" });
            _diagPeriodSec = CreateParameter("Diag period (sec, 0=off)", 60, 0, 3600, 10);

            _tab.NewTickEvent += OnNewTick;

            _sendThread = new Thread(SendWorker) { IsBackground = true, Name = "PrevDayLvl_TgSender" };
            _sendThread.Start();

            _pollTimer = new Timer(Poll, null, 500, 500);

            DeleteEvent += OnDelete;

            // первое сообщение в лог — доказательство, что робот жив
            SendNewLogMessage("Робот запущен. Режим='" + _mode.ValueString
                + "', отступ=" + _offsetTicks.ValueInt + " тик(ов)", LogMessageType.System);

            TryLogLevels();
        }

        public override string GetNameStrategyType()
        {
            return "PreviousDayLevelCrossBot";
        }

        public override void ShowIndividualSettingsDialog()
        {
        }

        private void OnDelete()
        {
            try
            {
                _running = false;
                if (_pollTimer != null)
                {
                    _pollTimer.Dispose();
                    _pollTimer = null;
                }
            }
            catch { }
        }

        // =====================================================================
        // Settings helpers
        // =====================================================================

        private bool IsApproach
        {
            get
            {
                string m = _mode.ValueString;
                return string.IsNullOrEmpty(m)
                    || string.Equals(m.Trim(), "Approach", StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool IsRegimeOn
        {
            get
            {
                string r = _regime.ValueString;
                return string.IsNullOrEmpty(r)
                    || string.Equals(r.Trim(), "On", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void Log(LogMessageType type, string message)
        {
            Log(type, message, false);
        }

        private void Log(LogMessageType type, string message, bool isSignal)
        {
            try
            {
                string lvl = _logLevel == null ? "Verbose" : _logLevel.ValueString;

                if (string.Equals(lvl, "Silent", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (string.Equals(lvl, "ErrorsOnly", StringComparison.OrdinalIgnoreCase)
                    && type != LogMessageType.Error)
                {
                    return;
                }
                if (string.Equals(lvl, "Signals", StringComparison.OrdinalIgnoreCase)
                    && isSignal == false
                    && type != LogMessageType.Error)
                {
                    return;
                }

                SendNewLogMessage(message, type);
            }
            catch { }
        }

        // =====================================================================
        // Main loop
        // =====================================================================

        private void Poll(object state)
        {
            try
            {
                if (!_running)
                {
                    return;
                }
                if (!IsRegimeOn)
                {
                    return;
                }

                RefreshLevels();

                decimal price;
                DateTime time;
                if (TryGetPrice(out price, out time))
                {
                    CheckPrice(price, time);
                }

                DiagLog();
            }
            catch (Exception ex)
            {
                Log(LogMessageType.Error, "poll: " + ex.Message);
            }
        }

        private void OnNewTick(Trade trade)
        {
            try
            {
                if (!_running || !IsRegimeOn)
                {
                    return;
                }
                if (trade == null || trade.Price <= 0m)
                {
                    return;
                }

                RefreshLevels();
                CheckPrice(trade.Price, trade.Time);

                DiagLog();
            }
            catch (Exception ex)
            {
                Log(LogMessageType.Error, "tick: " + ex.Message);
            }
        }

        // =====================================================================
        // Levels: taken from the last finished candle, no CandleFinishedEvent
        // =====================================================================

        private void RefreshLevels()
        {
            List<Candle> done;

            try
            {
                done = _tab.CandlesFinishedOnly;
            }
            catch
            {
                return;
            }

            if (done == null || done.Count == 0)
            {
                return;
            }

            Candle prev = done[done.Count - 1];

            if (prev == null || prev.High <= 0m || prev.Low <= 0m)
            {
                return;
            }

            decimal step = 0m;
            try
            {
                if (_tab.Security != null)
                {
                    step = _tab.Security.PriceStep;
                }
            }
            catch { }

            decimal offset = step > 0m ? _offsetTicks.ValueInt * step : 0m;
            bool approach = IsApproach;

            decimal trigHigh = approach ? prev.High - offset : prev.High + offset;
            decimal trigLow = approach ? prev.Low + offset : prev.Low - offset;

            string label = GetPeriodLabel(done);

            lock (_lock)
            {
                if (prev.TimeStart == _levelsBarTime)
                {
                    return;
                }

                _levelsBarTime = prev.TimeStart;
                _prevHigh = prev.High;
                _prevLow = prev.Low;
                _prevClose = prev.Close;
                _trigHigh = trigHigh;
                _trigLow = trigLow;
                _offset = offset;
                _periodLabel = label;
                _highAlerted = false;
                _lowAlerted = false;
            }

            Log(LogMessageType.System,
                "Уровни (" + SecurityName + "): H=" + Fmt(prev.High)
                + ", L=" + Fmt(prev.Low)
                + ", trigH=" + Fmt(trigHigh)
                + ", trigL=" + Fmt(trigLow)
                + ", бар " + prev.TimeStart.ToString("yyyy-MM-dd HH:mm")
                + ", " + label);
        }

        private void TryLogLevels()
        {
            try
            {
                RefreshLevels();
            }
            catch { }
        }

        // =====================================================================
        // Check price against levels
        // =====================================================================

        private void CheckPrice(decimal price, DateTime time)
        {
            if (price <= 0m)
            {
                return;
            }

            bool fireHigh = false;
            bool fireLow = false;

            decimal trigHigh;
            decimal trigLow;
            decimal prevHigh;
            decimal prevLow;
            string label;

            lock (_lock)
            {
                if (_levelsBarTime == DateTime.MinValue)
                {
                    return;
                }
                if (_trigHigh <= 0m || _trigLow <= 0m)
                {
                    return;
                }

                // Срабатывание по состоянию: цена уже за порогом,
                // по этому уровню в этом баре алерта ещё не было.
                if (!_highAlerted && price >= _trigHigh)
                {
                    _highAlerted = true;
                    fireHigh = true;
                }

                if (!_lowAlerted && price <= _trigLow)
                {
                    _lowAlerted = true;
                    fireLow = true;
                }

                trigHigh = _trigHigh;
                trigLow = _trigLow;
                prevHigh = _prevHigh;
                prevLow = _prevLow;
                label = _periodLabel;
                _lastPrice = price;
            }

            if (fireHigh)
            {
                string msg = BuildMessage("МАКСИМУМА", prevHigh, trigHigh, price, time, label);
                Log(LogMessageType.System, "ALERT HIGH: trig=" + Fmt(trigHigh) + ", price=" + Fmt(price), true);
                _sendQueue.Enqueue(msg);
            }

            if (fireLow)
            {
                string msg = BuildMessage("МИНИМУМА", prevLow, trigLow, price, time, label);
                Log(LogMessageType.System, "ALERT LOW: trig=" + Fmt(trigLow) + ", price=" + Fmt(price), true);
                _sendQueue.Enqueue(msg);
            }
        }

        private string BuildMessage(string type, decimal level, decimal trig, decimal cur,
                                    DateTime time, string periodLabel)
        {
            bool approach = IsApproach;

            string head = approach
                ? "⚠️ ПОДХОД К " + type + " " + (periodLabel.ToUpper()) + "!"
                : "🔔 ПРОБОЙ " + type + " " + (periodLabel.ToUpper()) + "!";

            string offsetInfo = _offsetTicks.ValueInt > 0
                ? "Отступ: " + _offsetTicks.ValueInt + " тик(ов) = " + Fmt(_offset)
                : "Отступ: без отступа";

            string trigLabel = approach ? "Порог подхода" : "Порог срабатывания";

            return head
                + "\nИнструмент: " + SecurityName
                + "\nHigh/Low бара: " + Fmt(level)
                + "\n" + offsetInfo
                + "\n" + trigLabel + ": " + Fmt(trig)
                + "\nТекущая цена: " + Fmt(cur)
                + "\nВремя: " + time.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // =====================================================================
        // Price source
        // =====================================================================

        private bool TryGetPrice(out decimal price, out DateTime time)
        {
            price = 0m;
            time = DateTime.Now;

            try
            {
                List<Trade> trades = _tab.Trades;

                if (trades != null && trades.Count > 0)
                {
                    Trade t = trades[trades.Count - 1];

                    if (t != null && t.Price > 0m)
                    {
                        price = t.Price;
                        time = t.Time;
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                List<Candle> all = _tab.CandlesAll;

                if (all != null && all.Count > 0)
                {
                    Candle c = all[all.Count - 1];

                    if (c != null && c.Close > 0m)
                    {
                        price = c.Close;
                        time = c.TimeStart;
                        return true;
                    }
                }
            }
            catch { }

            decimal ask = 0m;
            decimal bid = 0m;

            try
            {
                ask = _tab.PriceBestAsk;
                bid = _tab.PriceBestBid;
            }
            catch { }

            if (ask > 0m && bid > 0m)
            {
                price = (ask + bid) / 2m;
                return true;
            }
            if (ask > 0m)
            {
                price = ask;
                return true;
            }
            if (bid > 0m)
            {
                price = bid;
                return true;
            }

            return false;
        }

        // =====================================================================
        // Diagnostics
        // =====================================================================

        private void DiagLog()
        {
            int period = _diagPeriodSec == null ? 0 : _diagPeriodSec.ValueInt;

            if (period <= 0)
            {
                return;
            }

            if (_lastDiag != DateTime.MinValue
                && (DateTime.Now - _lastDiag).TotalSeconds < period)
            {
                return;
            }

            _lastDiag = DateTime.Now;

            int doneCount = 0;
            int allCount = 0;
            string lastCandleTime = "-";

            try
            {
                List<Candle> done = _tab.CandlesFinishedOnly;
                if (done != null)
                {
                    doneCount = done.Count;
                }
            }
            catch { }

            try
            {
                List<Candle> all = _tab.CandlesAll;
                if (all != null && all.Count > 0)
                {
                    allCount = all.Count;
                    lastCandleTime = all[all.Count - 1].TimeStart.ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch { }

            string levels;
            decimal price;

            lock (_lock)
            {
                levels = _levelsBarTime == DateTime.MinValue
                    ? "нет (баров нет)"
                    : _levelsBarTime.ToString("yyyy-MM-dd HH:mm")
                      + ", trigH=" + Fmt(_trigHigh)
                      + ", trigL=" + Fmt(_trigLow);
                price = _lastPrice;
            }

            string status;
            try
            {
                status = "events=" + _tab.EventsIsOn + ", server=" + _tab.ServerStatus;
            }
            catch
            {
                status = "events=?, server=?";
            }

            Log(LogMessageType.System,
                "DIAG (" + SecurityName + "): баров=" + doneCount
                + ", свечей=" + allCount
                + ", последняя=" + lastCandleTime
                + ", уровни=" + levels
                + ", цена=" + Fmt(price)
                + ", " + status);
        }

        // =====================================================================
        // Period label
        // =====================================================================

        private string GetPeriodLabel(List<Candle> done)
        {
            try
            {
                if (done == null || done.Count < 2)
                {
                    return "предыдущего бара";
                }

                double minutes = (done[done.Count - 1].TimeStart - done[done.Count - 2].TimeStart).TotalMinutes;

                if (minutes >= 28 * 1440) return "предыдущего месяца";
                if (minutes >= 7 * 1440) return "предыдущей недели";
                if (minutes >= 18 * 60) return "предыдущего дня";
                if (minutes >= 210) return "предыдущей 4-часовки";
                if (minutes >= 50) return "предыдущего часа";
                if (minutes >= 25) return "предыдущей 30-минутки";
                if (minutes >= 12) return "предыдущей 15-минутки";
                if (minutes >= 4) return "предыдущей 5-минутки";

                return "предыдущей минутки";
            }
            catch
            {
                return "предыдущего бара";
            }
        }

        // =====================================================================
        // Telegram sending
        // =====================================================================

        private void SendWorker()
        {
            while (_running)
            {
                try
                {
                    string message;

                    if (_sendQueue.TryDequeue(out message))
                    {
                        SendSync(message);
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }

        private void SendSync(string message)
        {
            string token = (_telegramToken.ValueString ?? "").Trim();
            string chatId = (_telegramChatId.ValueString ?? "").Trim();

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            {
                Log(LogMessageType.Error, "Telegram: не заданы Token / Chat ID");
                return;
            }

            try
            {
                string url = "https://api.telegram.org/bot" + token + "/sendMessage";

                FormUrlEncodedContent content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("chat_id", chatId),
                    new KeyValuePair<string, string>("text", message)
                });

                HttpResponseMessage response = _httpClient.PostAsync(url, content).GetAwaiter().GetResult();

                if (response.IsSuccessStatusCode)
                {
                    Log(LogMessageType.System, "Telegram: отправлено");
                }
                else
                {
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Log(LogMessageType.Error, "Telegram: HTTP " + (int)response.StatusCode + ": " + body);
                }
            }
            catch (Exception ex)
            {
                Log(LogMessageType.Error, "Telegram: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // =====================================================================
        // Formatting helpers
        // =====================================================================

        private string SecurityName
        {
            get
            {
                try
                {
                    if (_tab != null && _tab.Security != null)
                    {
                        return _tab.Security.Name;
                    }
                }
                catch { }

                return "?";
            }
        }

        private string Fmt(decimal value)
        {
            int decimals = 2;

            try
            {
                if (_tab != null && _tab.Security != null && _tab.Security.Decimals > 0)
                {
                    decimals = _tab.Security.Decimals;
                }
            }
            catch { }

            return value.ToString("F" + decimals);
        }
    }
}
