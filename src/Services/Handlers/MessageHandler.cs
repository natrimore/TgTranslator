﻿using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TgTranslator.Data.Options;
using TgTranslator.Exceptions;
using TgTranslator.Extensions;
using TgTranslator.Interfaces;
using TgTranslator.Menu;
using TgTranslator.Validation;

namespace TgTranslator.Services.Handlers
{
    public class MessageHandler : IMessageHandler
    {
        private readonly Blacklists _blacklist;
        private readonly BotMenu _botMenu;

        private readonly string _botUsername;
        private readonly TelegramBotClient _client;
        private readonly ILanguageDetector _languageDetector;
        private readonly IMetrics _metrics;
        private readonly SettingsProcessor _settingsProcessor;
        private readonly ITranslator _translator;
        private readonly MessageValidator _validator;
        private readonly UsersDatabaseService _users;
        private readonly GroupsBlacklistService _groupsBlacklist;
        
        public MessageHandler(TelegramBotClient client, BotMenu botMenu, SettingsProcessor settingsProcessor, ILanguageDetector languageLanguageDetector, 
            ITranslator translator, IMetrics metrics, IOptions<Blacklists> blacklistsOptions, MessageValidator validator, UsersDatabaseService users, GroupsBlacklistService groupsBlacklist)
        {
            _client = client;
            _botMenu = botMenu;
            _settingsProcessor = settingsProcessor;
            _translator = translator;
            _metrics = metrics;
            _languageDetector = languageLanguageDetector;
            _blacklist = blacklistsOptions.Value;
            _validator = validator;
            _users = users;
            _groupsBlacklist = groupsBlacklist;

            _botUsername = _client.GetMeAsync().Result.Username;
        }

        #region IMessageHandler Members

        public async Task HandleMessageAsync(Message message)
        {
            _metrics.HandleMessage();
            Log.Information("Got new message {ChatId} | {From}", message.Chat.Id, message.From);

            if (_blacklist.UserIdsBlacklist.Contains(message.From.Id))
                return;

            switch (message.Chat.Type)
            {
                case ChatType.Group:
                case ChatType.Supergroup:
                    await HandleGroupMessage(message);
                    break;

                case ChatType.Private:
                    await HandlePrivateMessage(message);
                    break;
            }
        }

        #endregion

        private async Task HandleGroupMessage(Message message)
        {
            _metrics.HandleGroupMessage(message.Chat.Id, string.IsNullOrEmpty(message.Text) ? 0 : message.Text.Length);
            if (!_validator.GroupMessageValid(message))
            {
                Log.Information("Message by {ChatId} | {From} is not valid", message.Chat.Id, message.From);
                return;
            }

            if (await _groupsBlacklist.InBlacklist(message.Chat.Id))
            {
                Log.Information("Group {ChatId} | {From} is in the blacklist", message.Chat.Id, message.From);
                return;
            }
            
            if (message.IsCommand())
            {
                Log.Information("Message by {ChatId} | {From} is a command", message.Chat.Id, message.From);
                await HandleCommand(message);
                return;
            }

            if (message.Text.StartsWith($"@{_botUsername} set:"))
            {
                Log.Information("Message by {ChatId} | {From} is a setting changing", message.Chat.Id, message.From);
                await HandleSettingChanging(message);
                return;
            }

            switch (await _settingsProcessor.GetTranslationMode(message.Chat.Id))
            {
                case TranslationMode.Forwards:
                    Log.Information("Group {ChatId} | {From} is using Forwards translation mode", message.Chat.Id, message.From);
                    if (message.ForwardSenderName == null)
                        return;
                    break;
                case TranslationMode.Manual:
                    Log.Information("Group {ChatId} | {From} is using Manual translation mode", message.Chat.Id, message.From);
                    if (message.ReplyToMessage == null) return;
                    if (message.Text == _botUsername || message.Text == "!translate" || await RequireTranslation(message.Text, await _settingsProcessor.GetGroupLanguage(message.Chat.Id)))
                    {
                        await HandleTranslation(message.ReplyToMessage);
                    }

                    return;
            }

            if (await RequireTranslation(message.Text, await _settingsProcessor.GetGroupLanguage(message.Chat.Id)))
                await HandleTranslation(message);
        }

        private async Task HandlePrivateMessage(Message message)
        {
            await _users.AddFromPmIfNeeded(message.From.Id, null);
            if (message.IsCommand())
            {
                await HandleCommand(message);
                return;
            }

            await _botMenu.SendMainMenu(message.Chat.Id);
        }

        private async Task HandleTranslation(Message message)
        {
            Log.Information("Handling translation for {ChatId} | {From}...", message.Chat.Id, message.From);
            _metrics.HandleTranslatorApiCall(message.Chat.Id, message.Text.Length);
            await _users.AddFromGroupIfNeeded(message.From.Id);
            
            string groupLanguage = await _settingsProcessor.GetGroupLanguage(message.Chat.Id);
            string translation = await _translator.TranslateTextAsync(message.Text, groupLanguage);

            if (string.IsNullOrEmpty(translation) || string.Equals(message.Text, translation, StringComparison.CurrentCultureIgnoreCase))
                return;

            await _client.SendTextMessageAsync(message.Chat.Id, translation, ParseMode.Default, true, true,
                message.MessageId);

            Log.Information("Sent translation to {ChatId} | {From}", message.Chat.Id, message.From);
        }

        private async Task HandleSettingChanging(Message message)
        {
            if (!await message.From.IsAdministrator(message.Chat.Id, _client))
                throw new UnauthorizedSettingChangingException();

            string tinyString = message.Text.Replace($"@{_botUsername} set:", "");

            (string param, string value) = (tinyString.Split('=')[0], tinyString.Split('=')[1]);

            if (!Enum.TryParse(param, true, out Setting setting) || !Enum.IsDefined(typeof(Setting), setting))
                throw new InvalidSettingException();

            if (!_settingsProcessor.ValidateSettings(Enum.Parse<Setting>(param, true), value))
                throw new InvalidSettingValueException();

            switch (setting)
            {
                case Setting.Language:
                    await _settingsProcessor.ChangeLanguage(message.Chat.Id, value);
                    break;
                case Setting.Mode:
                    await _settingsProcessor.ChangeMode(message.Chat.Id, Enum.Parse<TranslationMode>(value, true));
                    break;
            }

            await _client.SendTextMessageAsync(message.Chat.Id, "Done!", replyToMessageId: message.MessageId);
        }

        private async Task HandleCommand(Message message)
        {
            ChatType chatType = message.Chat.Type;
            string command = message.Text[1..];
            string payload = null;
            
            if (command.Contains("@"))
            {
                int indexOfAt = command.IndexOf('@');
                if(command.Substring(indexOfAt + 1) != _botUsername)
                    return;
                
                command = command[..indexOfAt];
            }

            if (command.Contains(" "))
            {
                payload = command.Split(" ")[1];
                command = command.Split(" ")[0];
            }

            switch (command)
            {
                case "settings" when chatType == ChatType.Group || chatType == ChatType.Supergroup:
                    if(!await message.From.IsAdministrator(message.Chat.Id, _client))
                        throw new UnauthorizedSettingChangingException();
                    
                    const string text = "You can access settings in the PM";
                    await _client.SendTextMessageAsync(message.Chat.Id, text, replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton
                    {
                        Text = "Open settings",
                        Url = $"https://t.me/{_botUsername}?start=s"
                    }));
                    
                    break;
                case "start" when chatType == ChatType.Private && !string.IsNullOrEmpty(payload):
                    if (payload != "s")
                        await _users.AddFromPmIfNeeded(message.From.Id, payload);
                    
                    await _botMenu.SendMainMenu(message.Chat.Id);
                    break;
                case "start" when chatType == ChatType.Private:
                case "settings" when chatType == ChatType.Private:
                    await _botMenu.SendMainMenu(message.Chat.Id);
                    break;
                case "help" when chatType == ChatType.Private:
                    await _client.SendVideoAsync(message.Chat.Id, "https://dubzer.dev/TgTranslatorHelp.mp4");
                    break;
                default:
                    return;
            }
        }

        private async Task<bool> RequireTranslation(string text, string mainLanguage)
        {
            #region Check if string contains only non-alphabetic chars

            var regex = new Regex(@"\P{L}{1,}");
            MatchCollection matches = regex.Matches(text);

            string match = matches.Count == 1 ? matches[0].Value : null;

            if (match != null && match.Length == text.Length)
                return false;

            #endregion

            string language = await _languageDetector.DetectLanguageAsync(text);

            return language != mainLanguage;
        }
    }
}