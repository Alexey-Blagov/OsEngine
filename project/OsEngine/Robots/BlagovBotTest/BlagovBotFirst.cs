using System;
using System.Collections.Generic;
using System.Text;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Market;
using OsEngine.Market.Connectors;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System.Threading;
using System.Diagnostics;


namespace OsEngine.Robots.BlagovBotTest
{
    [Bot("BlagovBotFirst")] //Атрибут класса 
    public class BlagovBotFirst : BotPanel
    {
        
        public BlagovBotFirst(string name, StartProgram startProgram) : base(name, startProgram)
        {
            //StrategyParameterButton button = CreateParameterButton("Hellow World button");
            //button.UserClickOnButtonEvent += Button_UserClickOnButtonEvent;
            
            //Создаем параметр String 
            StrategyParameterString regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "Others" });

            //Создаем параметр Int 
            StrategyParameterInt smaLengh = CreateParameter("SMA Lenth = ", 15, 1, 20, 1);
           
            //Создаем параметр Bool 
            StrategyParameterBool isUpCandleToEntry = CreateParameter("Is Up Candle", true);

            //Создаем параметр decimal 
            StrategyParameterDecimal bolingerDeviation = CreateParameter("Bolinger deviation", 1.4m, 1, 2, 0.1m);

            //Создаем параметр TimeOfDay 
            StrategyParameterTimeOfDay startToTrade = CreateParameterTimeOfDay("STart to trade: ", 11, 00, 00, 00); 

        }
        //Выводим в лог сообщение  при нажатии на кнопку
        private void Button_UserClickOnButtonEvent()
        {
            SendNewLogMessage("Hello world", Logging.LogMessageType.Error); 
        }

        public override string GetNameStrategyType()
        {

            return "Hello World"; 
        
        }
        public override void ShowIndividualSettingsDialog()
        {
            
        }
    }
}
