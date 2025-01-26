using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Brigadier.NET;
using Brigadier.NET.Builder;
using MinecraftClient.CommandHandler;
using MinecraftClient.CommandHandler.Patch;
using MinecraftClient.Commands;
using MinecraftClient.Inventory;
using MinecraftClient.Mapping;
using MinecraftClient.Protocol.Handlers;
using MinecraftClient.Scripting;
using Tomlet.Attributes;

namespace MinecraftClient.ChatBots
{
    public class Farmer : ChatBot
    {
        public const string CommandName = "farmer"; // 定义命令名称

        public static Configs Config = new(); // 配置实例

        [TomlDoNotInlineObject]
        public class Configs
        {
            [NonSerialized] private const string BotName = "Farmer"; // 机器人名称

            public bool Enabled = false; // 是否启用

            [TomlInlineComment("$ChatBot.Farmer.Delay_Between_Tasks$")]
            public double Delay_Between_Tasks = 1.0; // 任务之间的延迟

            public void OnSettingUpdate()
            {
                if (Delay_Between_Tasks < 1.0)
                    Delay_Between_Tasks = 1.0; // 确保延迟不小于1秒
            }
        }

        private enum State
        {
            SearchingForCropsToBreak = 0, // 搜索要破坏的作物
            SearchingForFarmlandToPlant, // 搜索要种植的农田
            BoneMealingCrops, // 使用骨粉
            CollectingItems // 收集物品
        }

        public enum CropType
        {
            Beetroot,
            Carrot,
            Melon,
            NetherWart,
            Pumpkin,
            Potato,
            Wheat
        }

        private State state = State.SearchingForCropsToBreak; // 初始状态
        private CropType cropType = CropType.Wheat; // 默认作物类型
        private int farmingRadius = 30; // 农业半径
        private bool running = false; // 是否运行
        private bool allowUnsafe = false; // 是否允许不安全操作
        private bool allowTeleport = false; // 是否允许传送
        private bool debugEnabled = false; // 是否启用调试

        private int Delay_Between_Tasks_Millisecond => (int)Math.Round(Config.Delay_Between_Tasks * 1000); // 任务之间的延迟（毫秒）

        private const string commandDescription =
            "farmer <start <crop type> [radius:<radius = 30>] [unsafe:<true/false>] [teleport:<true/false>] [debug:<true/false>]|stop>"; // 命令描述

        public override void Initialize()
        {
            if (GetProtocolVersion() < Protocol18Handler.MC_1_13_Version)
            {
                LogToConsole(Translations.bot_farmer_not_implemented); // 如果协议版本低于1.13，记录日志并返回
                return;
            }

            if (!GetTerrainEnabled())
            {
                LogToConsole(Translations.bot_farmer_needs_terrain); // 如果未启用地形，记录日志并返回
                return;
            }

            if (!GetInventoryEnabled())
            {
                LogToConsole(Translations.bot_farmer_needs_inventory); // 如果未启用库存，记录日志并返回
                return;
            }

            // 注册帮助命令
            McClient.dispatcher.Register(l => l.Literal("help")
                .Then(l => l.Literal(CommandName)
                    .Executes(r => OnCommandHelp(r.Source, string.Empty))
                )
            );

            // 注册农民命令
            McClient.dispatcher.Register(l => l.Literal(CommandName)
                .Then(l => l.Literal("stop")
                    .Executes(r => OnCommandStop(r.Source)))
                .Then(l => l.Literal("start")
                    .Then(l => l.Argument("CropType", MccArguments.FarmerCropType())
                        .Executes(r => OnCommandStart(r.Source, MccArguments.GetFarmerCropType(r, "CropType"), null))
                        .Then(l => l.Argument("OtherArgs", Arguments.GreedyString())
                            .Executes(r => OnCommandStart(r.Source, MccArguments.GetFarmerCropType(r, "CropType"),
                                Arguments.GetString(r, "OtherArgs"))))))
                .Then(l => l.Literal("_help")
                    .Executes(r => OnCommandHelp(r.Source, string.Empty))
                    .Redirect(McClient.dispatcher.GetRoot().GetChild("help").GetChild(CommandName)))
            );
        }

        public override void OnUnload()
        {
            running = false; // 停止运行
            BotMovementLock.Instance?.UnLock("Farmer"); // 解锁移动
            McClient.dispatcher.Unregister(CommandName); // 注销命令
            McClient.dispatcher.GetRoot().GetChild("help").RemoveChild(CommandName); // 从帮助中移除命令
        }

        private int OnCommandHelp(CmdResult r, string? cmd)
        {
            return r.SetAndReturn(cmd switch
            {
#pragma warning disable format // @formatter:off
                _           =>   Translations.bot_farmer_desc + ": " + commandDescription
                                   + '\n' + McClient.dispatcher.GetAllUsageString(CommandName, false),
#pragma warning restore format // @formatter:on
            });
        }

        private int OnCommandStop(CmdResult r)
        {
            if (!running)
            {
                return r.SetAndReturn(CmdResult.Status.Fail, Translations.bot_farmer_already_stopped); // 如果已经停止，返回失败状态
            }
            else
            {
                running = false; // 停止运行
                return r.SetAndReturn(CmdResult.Status.Done, Translations.bot_farmer_stopping); // 返回成功状态
            }
        }

        private int OnCommandStart(CmdResult r, CropType whatToFarm, string? otherArgs)
        {
            if (running)
                return r.SetAndReturn(CmdResult.Status.Fail, Translations.bot_farmer_already_running); // 如果已经运行，返回失败状态

            var movementLock = BotMovementLock.Instance;
            if (movementLock is { IsLocked: true })
                return r.SetAndReturn(CmdResult.Status.Fail,
                    string.Format(Translations.bot_common_movement_lock_held, "Farmer", movementLock.LockedBy)); // 如果移动被锁定，返回失败状态

            var radius = 30; // 默认半径

            state = State.SearchingForFarmlandToPlant; // 设置初始状态
            cropType = whatToFarm; // 设置作物类型
            allowUnsafe = false; // 默认不允许不安全操作
            allowTeleport = false; // 默认不允许传送
            debugEnabled = false; // 默认不启用调试

            if (!string.IsNullOrWhiteSpace(otherArgs))
            {
                var args = otherArgs.ToLower().Split(' ', StringSplitOptions.TrimEntries);
                foreach (var currentArg in args)
                {
                    if (!currentArg.Contains(':'))
                    {
                        LogToConsole(
                            $"§§6§1§0{string.Format(Translations.bot_farmer_warining_invalid_parameter, currentArg)}");
                        continue;
                    }

                    var parts = currentArg.Split(":", StringSplitOptions.TrimEntries);

                    if (parts.Length != 2)
                    {
                        LogToConsole(
                            $"§§6§1§0{string.Format(Translations.bot_farmer_warining_invalid_parameter, currentArg)}");
                        continue;
                    }

                    switch (parts[0])
                    {
                        case "r":
                        case "radius":
                            if (!int.TryParse(parts[1], NumberStyles.Any, CultureInfo.CurrentCulture, out radius))
                                LogToConsole($"§§6§1§0{Translations.bot_farmer_invalid_radius}");

                            if (radius <= 0)
                            {
                                LogToConsole($"§§6§1§0{Translations.bot_farmer_invalid_radius}");
                                radius = 30;
                            }

                            break;

                        case "f":
                        case "unsafe":
                            if (allowUnsafe)
                                break;

                            if (parts[1].Equals("true") || parts[1].Equals("1"))
                            {
                                LogToConsole($"§§6§1§0{Translations.bot_farmer_warining_force_unsafe}");
                                allowUnsafe = true;
                            }
                            else allowUnsafe = false;

                            break;

                        case "t":
                        case "teleport":
                            if (allowTeleport)
                                break;

                            if (parts[1].Equals("true") || parts[1].Equals("1"))
                            {
                                LogToConsole($"§§4§1§f{Translations.bot_farmer_warining_allow_teleport}");
                                allowTeleport = true;
                            }
                            else allowTeleport = false;

                            break;

                        case "d":
                        case "debug":
                            if (debugEnabled)
                                break;

                            if (parts[1].Equals("true") || parts[1].Equals("1"))
                            {
                                LogToConsole("调试已启用！");
                                debugEnabled = true;
                            }
                            else debugEnabled = false;

                            break;
                    }
                }
            }

            farmingRadius = radius; // 设置农业半径
            running = true; // 设置为运行状态
            new Thread(() => MainProcess()).Start(); // 启动主进程线程

            return r.SetAndReturn(CmdResult.Status.Done); // 返回成功状态
        }

        public override void AfterGameJoined()
        {
            BotMovementLock.Instance?.UnLock("Farmer"); // 解锁移动
            running = false; // 设置为未运行状态
        }

        public override bool OnDisconnect(DisconnectReason reason, string message)
        {
            BotMovementLock.Instance?.UnLock("Farmer"); // 解锁移动
            running = false; // 设置为未运行状态
            return true; // 返回true表示处理断开连接
        }

        private void MainProcess()
        {
            var movementLock = BotMovementLock.Instance;
            switch (movementLock)
            {
                case { IsLocked: false }:
                    if (!movementLock.Lock("Farmer"))
                    {
                        running = false;
                        LogToConsole($"§§6§1§0农民机器人由于某种原因未能获得移动锁！");
                        LogToConsole($"§§6§1§0禁用其他具有移动机制的机器人，然后重试！");
                        return;
                    }

                    LogDebug($"已锁定其他机器人的移动！");
                    break;
                case { IsLocked: true }:
                    running = false;
                    LogToConsole($"§§6§1§0农民机器人由于某种原因未能获得移动锁！");
                    LogToConsole($"§§6§1§0禁用其他具有移动机制的机器人，然后重试！");
                    return;
            }

            LogToConsole($"§§2§1§f{Translations.bot_farmer_started}");
            LogToConsole($"§§2§1§f {Translations.bot_farmer_crop_type}: {cropType}");
            LogToConsole($"§§2§1§f {Translations.bot_farmer_radius}: {farmingRadius}");

            var itemTypes = new List<ItemType>
            {
                GetSeedItemTypeForCropType(cropType),
                GetCropItemTypeForCropType(cropType)
            };
            itemTypes = itemTypes.Distinct().ToList();

            while (running)
            {
                // 如果机器人正在吃东西，不做任何事情，等待1秒
                if (AutoEat.Eating)
                {
                    LogDebug("正在吃东西...");
                    Thread.Sleep(Delay_Between_Tasks_Millisecond);
                    continue;
                }

                switch (state)
                {
                    case State.SearchingForFarmlandToPlant:
                        LogDebug("寻找农田...");

                        var cropTypeToPlant = GetSeedItemTypeForCropType(cropType);

                        // 如果热键栏中没有种子，跳过这一步并尝试收集一些
                        if (!SwitchToItem(cropTypeToPlant))
                        {
                            LogDebug("没有种子，尝试寻找一些作物来破坏");
                            state = State.SearchingForCropsToBreak;
                            Thread.Sleep(Delay_Between_Tasks_Millisecond);
                            continue;
                        }

                        var farmlandToPlantOn = FindEmptyFarmland(farmingRadius);

                        if (farmlandToPlantOn.Count == 0)
                        {
                            LogDebug("找不到任何农田，尝试寻找一些作物来破坏");
                            state = State.SearchingForCropsToBreak;
                            Thread.Sleep(Delay_Between_Tasks_Millisecond);
                            continue;
                        }

                        var i = 0;
                        foreach (var location in farmlandToPlantOn.TakeWhile(location => running))
                        {
                            // 每隔一次迭代检查一次，微小的优化
                            if (i % 2 == 0)
                            {
                                if (!HasItemOfTypeInInventory(cropTypeToPlant))
                                {
                                    LogDebug("种子用完了，寻找作物来破坏...");
                                    state = State.SearchingForCropsToBreak;
                                    Thread.Sleep(Delay_Between_Tasks_Millisecond);
                                    continue;
                                }
                            }

                            var yValue = Math.Floor(location.Y) + 1;

                            // TODO: 找出为什么这不起作用。
                            // 为什么需要这个：有时服务器会因为“无效移动”数据包踢出玩家。
                            /*if (cropType == CropType.NetherWart)
                                yValue = (double)(Math.Floor(location.Y) - 1.0) + (double)0.87500;*/

                            var location2 = new Location(Math.Floor(location.X) + 0.5, yValue,
                                Math.Floor(location.Z) + 0.5);

                            if (WaitForMoveToLocation(location2))
                            {
                                LogDebug("移动到: " + location2);

                                // 如果没有更多的种子，停止
                                if (!SwitchToItem(GetSeedItemTypeForCropType(cropType)))
                                {
                                    LogDebug("没有种子，尝试寻找一些作物来破坏");
                                    break;
                                }

                                var loc = new Location(Math.Floor(location.X), Math.Floor(location2.Y),
                                    Math.Floor(location.Z));
                                LogDebug("发送放置方块到: " + loc);

                                SendPlaceBlock(loc, Direction.Up);
                                Thread.Sleep(300);
                            }
                            else LogDebug("无法移动到: " + location2);

                            i++;
                        }

                        LogDebug("完成种植作物！");
                        state = State.SearchingForCropsToBreak;
                        break;

                    case State.SearchingForCropsToBreak:
                        LogDebug("寻找作物来破坏...");

                        var cropsToCollect = findCrops(farmingRadius, cropType, true);

                        if (cropsToCollect.Count == 0)
                        {
                            // LogToConsole("没有作物可破坏，尝试用骨粉处理未成熟的作物");
                            state = State.BoneMealingCrops;
                            Thread.Sleep(Delay_Between_Tasks_Millisecond);
                            continue;
                        }

                        // 如果机器人在库存中有斧头，切换到斧头以更快地破坏
                        if (cropType is CropType.Melon or CropType.Pumpkin)
                        {
                            // 从钻石斧头开始，如果没有找到，尝试较低等级的斧头
                            var switched = SwitchToItem(ItemType.DiamondAxe);

                            if (!switched)
                                switched = SwitchToItem(ItemType.IronAxe);

                            if (!switched)
                                switched = SwitchToItem(ItemType.GoldenAxe);

                            if (!switched)
                                SwitchToItem(ItemType.StoneAxe);
                        }

                        foreach (var location in cropsToCollect.TakeWhile(location => running))
                        {
                            // C# 将其舍入到 0.94
                            // 当机器人用骨粉处理处于生长第一阶段的胡萝卜或土豆时，这将是必要的，
                            // 因为有时机器人会走过作物并破坏它们
                            // TODO: 找到修复方法
                            // new Location(Math.Floor(location.X) + 0.5, (double)((location.Y - 1) + (double)0.93750), Math.Floor(location.Z) + 0.5)

                            if (WaitForMoveToLocation(location))
                                WaitForDigBlock(location);

                            // 允许一些时间来拾取物品
                            Thread.Sleep(cropType is CropType.Melon or CropType.Pumpkin ? 400 : 200);
                        }

                        LogDebug("完成破坏作物！");
                        state = State.BoneMealingCrops;
                        break;

                    case State.BoneMealingCrops:
                        // 不能用骨粉处理
                        if (cropType == CropType.NetherWart)
                        {
                            state = State.SearchingForFarmlandToPlant;
                            Thread.Sleep(Delay_Between_Tasks_Millisecond);
                            continue;
                        }

                        // 如果热键栏中没有骨粉，跳过这一步
                        if (!SwitchToItem(ItemType.BoneMeal))
                        {
                            LogDebug("没有骨粉，寻找一些农田来种植种子");
                            state = State.SearchingForFarmlandToPlant;
                            Thread.Sleep(Delay_Between_Tasks_Millisecond);
                            continue;
                        }

                        var cropsToBonemeal = findCrops(farmingRadius, cropType, false);

                        if (cropsToBonemeal.Count == 0)
                        {
                            LogDebug("没有作物可用骨粉处理，寻找一些农田来种植种子");
                            state = State.SearchingForFarmlandToPlant;
                            Thread.Sleep(Delay_Between_Tasks_Millisecond);
                            continue;
                        }

                        var i2 = 0;
                        foreach (var location in cropsToBonemeal.TakeWhile(location => running))
                        {
                            // 每隔一次迭代检查一次，微小的优化
                            if (i2 % 2 == 0)
                            {
                                if (!HasItemOfTypeInInventory(ItemType.BoneMeal))
                                {
                                    LogDebug("骨粉用完了，寻找农田来种植...");
                                    state = State.SearchingForFarmlandToPlant;
                                    Thread.Sleep(Delay_Between_Tasks_Millisecond);
                                    continue;
                                }
                            }

                            if (WaitForMoveToLocation(location))
                            {
                                // 如果没有更多的骨粉，停止
                                if (!SwitchToItem(ItemType.BoneMeal))
                                {
                                    LogDebug("没有骨粉，寻找一些农田来种植种子...");
                                    break;
                                }

                                var location2 = new Location(Math.Floor(location.X) + 0.5, location.Y,
                                    Math.Floor(location.Z) + 0.5);
                                LogDebug("尝试用骨粉处理: " + location2);

                                // 发送大约4次骨粉尝试，应该可以用2-3次完成，但有时不行
                                for (var boneMealTimes = 0;
                                     boneMealTimes < (cropType == CropType.Beetroot ? 6 : 5);
                                     boneMealTimes++)
                                {
                                    // TODO: 检查胡萝卜/土豆是否处于生长第一阶段
                                    // 如果是，使用：new Location(location.X, (double)(location.Y - 1) + (double)0.93750, location.Z)
                                    SendPlaceBlock(location2, Direction.Down);
                                }

                                Thread.Sleep(100);
                            }

                            i2++;
                        }

                        LogDebug("完成用骨粉处理作物！");
                        state = State.CollectingItems;
                        break;

                    case State.CollectingItems:
                        LogDebug("寻找物品来收集...");

                        var currentLocation = GetCurrentLocation();
                        var items = GetEntities()
                            .Where(x =>
                                x.Value.Type == EntityType.Item &&
                                x.Value.Location.Distance(currentLocation) <= farmingRadius &&
                                itemTypes.Contains(x.Value.Item.Type))
                            .Select(x => x.Value)
                            .ToList();
                        items = items.OrderBy(x => x.Location.Distance(currentLocation)).ToList();

                        if (items.Any())
                        {
                            LogDebug("收集物品...");

                            foreach (var entity in items.TakeWhile(entity => running))
                                WaitForMoveToLocation(entity.Location);

                            LogDebug("完成收集物品！");
                        }
                        else LogDebug("没有物品可收集！");

                        state = State.SearchingForFarmlandToPlant;
                        break;
                }

                LogDebug($"等待 {Config.Delay_Between_Tasks:0.00} 秒进行下一轮循环。");
                Thread.Sleep(Delay_Between_Tasks_Millisecond);
            }

            movementLock?.UnLock("Farmer");
            LogDebug($"已解锁其他机器人的移动！");
            LogToConsole(Translations.bot_farmer_stopped);
        }

        private static Material GetMaterialForCropType(CropType type)
        {
            return type switch
            {
                CropType.Beetroot => Material.Beetroots,
                CropType.Carrot => Material.Carrots,
                CropType.Melon => Material.Melon,
                CropType.NetherWart => Material.NetherWart,
                CropType.Pumpkin => Material.Pumpkin,
                CropType.Potato => Material.Potatoes,
                CropType.Wheat => Material.Wheat,
                _ => throw new Exception("Material type for " + type.GetType().Name + " has not been mapped!")
            };
        }

        private static ItemType GetSeedItemTypeForCropType(CropType type)
        {
            return type switch
            {
                CropType.Beetroot => ItemType.BeetrootSeeds,
                CropType.Carrot => ItemType.Carrot,
                CropType.Melon => ItemType.MelonSeeds,
                CropType.NetherWart => ItemType.NetherWart,
                CropType.Pumpkin => ItemType.PumpkinSeeds,
                CropType.Potato => ItemType.Potato,
                CropType.Wheat => ItemType.WheatSeeds,
                _ => throw new Exception("Seed type for " + type.GetType().Name + " has not been mapped!")
            };
        }

        private static ItemType GetCropItemTypeForCropType(CropType type)
        {
            return type switch
            {
                CropType.Beetroot => ItemType.Beetroot,
                CropType.Carrot => ItemType.Carrot,
                CropType.Melon => ItemType.Melon,
                CropType.NetherWart => ItemType.NetherWart,
                CropType.Pumpkin => ItemType.Pumpkin,
                CropType.Potato => ItemType.Potato,
                CropType.Wheat => ItemType.Wheat,
                _ => throw new Exception("Item type for " + type.GetType().Name + " has not been mapped!")
            };
        }

        private List<Location> FindEmptyFarmland(int radius)
        {
            return GetWorld()
                .FindBlock(GetCurrentLocation(),
                    cropType == CropType.NetherWart ? Material.SoulSand : Material.Farmland, radius)
                .Where(location => GetWorld().GetBlock(new Location(location.X, location.Y + 1, location.Z)).Type ==
                                   Material.Air)
                .ToList();
        }

        private List<Location> findCrops(int radius, CropType cropType, bool fullyGrown)
        {
            var material = GetMaterialForCropType(cropType);

            // A bit of a hack to enable bone mealing melon and pumpkin stems
            if (!fullyGrown && cropType is CropType.Melon or CropType.Pumpkin)
                material = cropType == CropType.Melon ? Material.MelonStem : Material.PumpkinStem;

            return GetWorld()
                .FindBlock(GetCurrentLocation(), material, radius)
                .Where(location =>
                {
                    if (fullyGrown && material is Material.Melon or Material.Pumpkin)
                        return true;

                    var isFullyGrown = IsCropFullyGrown(GetWorld().GetBlock(location), cropType);
                    return fullyGrown ? isFullyGrown : !isFullyGrown;
                })
                .ToList();
        }

        private bool IsCropFullyGrown(Block block, CropType cropType)
        {
            var protocolVersion = GetProtocolVersion();

            switch (cropType)
            {
                case CropType.Beetroot:
                    switch (protocolVersion)
                    {
                        case Protocol18Handler.MC_1_20_Version when block.BlockId == 12371:
                        case Protocol18Handler.MC_1_19_4_Version when block.BlockId == 12356:
                        case Protocol18Handler.MC_1_19_3_Version when block.BlockId == 11887:
                        case >= Protocol18Handler.MC_1_19_Version and <= Protocol18Handler.MC_1_19_2_Version
                            when block.BlockId == 10103:
                        case >= Protocol18Handler.MC_1_17_Version and <= Protocol18Handler.MC_1_18_2_Version
                            when block.BlockId == 9472:
                        case >= Protocol18Handler.MC_1_16_Version and <= Protocol18Handler.MC_1_16_5_Version
                            when block.BlockId == 9226:
                        case >= Protocol18Handler.MC_1_14_Version and <= Protocol18Handler.MC_1_15_2_Version
                            when block.BlockId == 8686:
                        case >= Protocol18Handler.MC_1_13_Version and < Protocol18Handler.MC_1_14_Version
                            when block.BlockId == 8162:
                            return true;
                    }

                    break;

                case CropType.Carrot:
                    switch (protocolVersion)
                    {
                        case Protocol18Handler.MC_1_20_Version when block.BlockId == 8602:
                        case Protocol18Handler.MC_1_19_4_Version when block.BlockId == 8598:
                        case Protocol18Handler.MC_1_19_3_Version when block.BlockId == 8370:
                        case >= Protocol18Handler.MC_1_19_Version and <= Protocol18Handler.MC_1_19_2_Version
                            when block.BlockId == 6930:
                        case >= Protocol18Handler.MC_1_17_Version and <= Protocol18Handler.MC_1_18_2_Version
                            when block.BlockId == 6543:
                        case >= Protocol18Handler.MC_1_16_Version and <= Protocol18Handler.MC_1_16_5_Version
                            when block.BlockId == 6341:
                        case >= Protocol18Handler.MC_1_14_Version and <= Protocol18Handler.MC_1_15_2_Version
                            when block.BlockId == 5801:
                        case >= Protocol18Handler.MC_1_13_Version and < Protocol18Handler.MC_1_14_Version
                            when block.BlockId == 5295:
                            return true;
                    }

                    break;

                // Checkin for stems and attached stems instead of Melons themselves
                case CropType.Melon:
                    switch (protocolVersion)
                    {
                        case Protocol18Handler.MC_1_20_Version when block.BlockId is 6836 or 6820:
                        case Protocol18Handler.MC_1_19_4_Version when block.BlockId is 6808 or 6606:
                        case Protocol18Handler.MC_1_19_3_Version when block.BlockId is 6582 or 6832:
                        case >= Protocol18Handler.MC_1_19_Version and <= Protocol18Handler.MC_1_19_2_Version
                            when block.BlockId is 5166 or 5150:
                        case >= Protocol18Handler.MC_1_17_Version and <= Protocol18Handler.MC_1_18_2_Version
                            when block.BlockId is 4860 or 4844:
                        case >= Protocol18Handler.MC_1_16_Version and <= Protocol18Handler.MC_1_16_5_Version
                            when block.BlockId is 4791 or 4775:
                        case >= Protocol18Handler.MC_1_14_Version and <= Protocol18Handler.MC_1_15_2_Version
                            when block.BlockId is 4771 or 4755:
                        case >= Protocol18Handler.MC_1_13_Version and < Protocol18Handler.MC_1_14_Version
                            when block.BlockId is 4268 or 4252:
                            return true;
                    }

                    break;

                case CropType.NetherWart:
                    switch (protocolVersion)
                    {
                        case Protocol18Handler.MC_1_20_Version when block.BlockId == 7388:
                        case Protocol18Handler.MC_1_19_4_Version when block.BlockId == 7384:
                        case Protocol18Handler.MC_1_19_3_Version when block.BlockId == 7158:
                        case >= Protocol18Handler.MC_1_19_Version and <= Protocol18Handler.MC_1_19_2_Version
                            when block.BlockId == 5718:
                        case >= Protocol18Handler.MC_1_17_Version and <= Protocol18Handler.MC_1_18_2_Version
                            when block.BlockId == 5332:
                        case >= Protocol18Handler.MC_1_16_Version and <= Protocol18Handler.MC_1_16_5_Version
                            when block.BlockId == 5135:
                        case >= Protocol18Handler.MC_1_14_Version and <= Protocol18Handler.MC_1_15_2_Version
                            when block.BlockId == 5115:
                        case >= Protocol18Handler.MC_1_13_Version and < Protocol18Handler.MC_1_14_Version
                            when block.BlockId == 4612:
                            return true;
                    }

                    break;

                // Checkin for stems and attached stems instead of Pumpkins themselves
                case CropType.Pumpkin:
                    switch (protocolVersion)
                    {
                        case Protocol18Handler.MC_1_20_Version when block.BlockId is 5849 or 6816:
                        case Protocol18Handler.MC_1_19_4_Version when block.BlockId is 5845 or 6824:
                        case Protocol18Handler.MC_1_19_3_Version when block.BlockId is 5683 or 6598:
                        case >= Protocol18Handler.MC_1_19_Version and <= Protocol18Handler.MC_1_19_2_Version
                            when block.BlockId is 5158 or 5146:
                        case >= Protocol18Handler.MC_1_17_Version and <= Protocol18Handler.MC_1_18_2_Version
                            when block.BlockId is 4852 or 4840:
                        case >= Protocol18Handler.MC_1_16_Version and <= Protocol18Handler.MC_1_16_5_Version
                            when block.BlockId is 4783 or 4771:
                        case >= Protocol18Handler.MC_1_14_Version and <= Protocol18Handler.MC_1_15_2_Version
                            when block.BlockId is 4763 or 4751:
                        case >= Protocol18Handler.MC_1_13_Version and < Protocol18Handler.MC_1_14_Version
                            when block.BlockId is 4260 or 4248:
                            return true;
                    }

                    break;

                case CropType.Potato:
                    switch (protocolVersion)
                    {
                        case Protocol18Handler.MC_1_20_Version when block.BlockId == 8610:
                        case Protocol18Handler.MC_1_19_4_Version when block.BlockId == 8606:
                        case Protocol18Handler.MC_1_19_3_Version when block.BlockId == 8378:
                        case >= Protocol18Handler.MC_1_19_Version and <= Protocol18Handler.MC_1_19_2_Version
                            when block.BlockId == 6938:
                        case >= Protocol18Handler.MC_1_17_Version and <= Protocol18Handler.MC_1_18_2_Version
                            when block.BlockId == 6551:
                        case >= Protocol18Handler.MC_1_16_Version and <= Protocol18Handler.MC_1_16_5_Version
                            when block.BlockId == 6349:
                        case >= Protocol18Handler.MC_1_14_Version and <= Protocol18Handler.MC_1_15_2_Version
                            when block.BlockId == 5809:
                        case >= Protocol18Handler.MC_1_13_Version and < Protocol18Handler.MC_1_14_Version
                            when block.BlockId == 5303:
                            return true;
                    }

                    break;

                case CropType.Wheat:
                    switch (protocolVersion)
                    {
                        case Protocol18Handler.MC_1_20_Version when block.BlockId == 4285:
                        case Protocol18Handler.MC_1_19_4_Version when block.BlockId == 4281:
                        case Protocol18Handler.MC_1_19_3_Version when block.BlockId == 4233:
                        case >= Protocol18Handler.MC_1_19_Version and <= Protocol18Handler.MC_1_19_2_Version
                            when block.BlockId == 3619:
                        case >= Protocol18Handler.MC_1_17_Version and <= Protocol18Handler.MC_1_18_2_Version
                            when block.BlockId == 3421:
                        case >= Protocol18Handler.MC_1_16_Version and <= Protocol18Handler.MC_1_16_5_Version
                            when block.BlockId == 3364:
                        case >= Protocol18Handler.MC_1_14_Version and <= Protocol18Handler.MC_1_15_2_Version
                            when block.BlockId == 3362:
                        case >= Protocol18Handler.MC_1_13_Version and < Protocol18Handler.MC_1_14_Version
                            when block.BlockId == 3059:
                            return true;
                    }

                    break;
            }

            return false;
        }

        // Yoinked from ReinforceZwei's AutoTree and adapted to search the whole of inventory in additon to the hotbar
        private bool SwitchToItem(ItemType itemType)
        {
            var playerInventory = GetPlayerInventory();

            if (playerInventory.Items.TryGetValue(GetCurrentSlot() - 36, out var value) && value.Type == itemType)
                return true; // Already selected

            // Search the full inventory
            var fullInventorySearch = new List<int>(playerInventory.SearchItem(itemType));

            // Search for the seed in the hot bar
            var hotBarSearch = fullInventorySearch.Where(slot => slot is >= 36 and <= 44).ToList();

            if (hotBarSearch.Count > 0)
            {
                ChangeSlot((short)(hotBarSearch[0] - 36));
                return true;
            }

            if (fullInventorySearch.Count == 0)
                return false;

            var movingHelper = new ItemMovingHelper(playerInventory, Handler);
            movingHelper.Swap(fullInventorySearch[0], 36);
            ChangeSlot(0);

            return true;
        }

        // Yoinked from Daenges's Sugarcane Farmer
        private bool WaitForMoveToLocation(Location pos, float tolerance = 2f)
        {
            if (MoveToLocation(location: pos, allowUnsafe: allowUnsafe, allowDirectTeleport: allowTeleport))
            {
                LogDebug("移动到: " + pos);

                while (GetCurrentLocation().Distance(pos) > tolerance)
                    Thread.Sleep(200);

                return true;
            }
            else LogDebug("无法移动到: " + pos);

            return false;
        }

        // Yoinked from Daenges's Sugarcane Farmer
        private bool WaitForDigBlock(Location block, int digTimeout = 1000)
        {
            if (!DigBlock(block.ToFloor(), Direction.Down)) return false;
            short i = 0; // Maximum wait time of 10 sec.
            while (GetWorld().GetBlock(block).Type != Material.Air && i <= digTimeout)
            {
                Thread.Sleep(100);
                i++;
            }

            return i <= digTimeout;
        }

        private bool HasItemOfTypeInInventory(ItemType itemType)
        {
            return GetPlayerInventory().SearchItem(itemType).Length > 0;
        }

        private void LogDebug(object text)
        {
            if (debugEnabled)
                LogToConsole(text);
            else LogDebugToConsole(text);
        }
    }
}