using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SampleChecker.BLL.Dto;
using SampleChecker.BLL.Enum;
using SampleChecker.BLL.Kafka;
using SampleChecker.BLL.Redis;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SampleChecker.BLL
{
    public class NettingPartTwoStatusChecker : IConsumeProcess
    {
        private readonly ILogger _logger;
        private readonly IRedisService _redis;
        private readonly IKafkaSender _sender;
        private readonly IConfiguration _config;
        private readonly IRedisLogService _redisLog;

        public NettingPartTwoStatusChecker(ILogger<NettingPartTwoStatusChecker> logger, IConfiguration config, IRedisService redis, IKafkaSender sender, IRedisLogService redisLog)
        {
            _logger = logger;
            _redis = redis;
            _sender = sender;
            _config = config;
            _redisLog = redisLog;
        }

        public async Task ConsumeAsync<TKey>(ConsumeResult<TKey, string> consumeResult, CancellationToken cancellationToken = default)
        {
            var msgDto = JsonConvert.DeserializeObject<BaseMessageDto>(consumeResult.Message.Value);

            var trxId = msgDto.TrxId;
            var activity = msgDto.Activity;

            var logKey = $"LOG_STATUS_CHECKER_{activity}_{trxId}";
            var failedKey = $"FAILED_{activity}_{trxId}";
            var completedKey = $"COMPLETED_{activity}_{trxId}";
            var sourceKey = $"SOURCE_{activity}_{trxId}";
            try
            {
                var diff = await _redis.SetCombine(SetOperation.Difference, new string[] { sourceKey, completedKey, failedKey });

                if (!diff.Any())
                {
                    var isProcessed = await _redis.GetAsync<BaseMessageDto>(logKey);
                    if (null != isProcessed)
                    {
                        return;
                    }

                    await _redisLog.LogToRedis(msgDto, logKey, EnumStatus.Begin);

                    var failedMember = await _redis.GetSetMembersAsync(failedKey);
                    if (failedMember.Length > 0)
                    {
                        var msg = $"Netting process for {failedMember} data failed";
                        var failedMsg = SendMessage(trxId, activity, msg, EnumStatus.Failed);
                        var failedLog = _redisLog.LogToRedis(msgDto, logKey, EnumStatus.Failed);
                        await Task.WhenAll(failedMsg, failedLog);
                        return;
                    }
                    var completeMsg = SendMessage(trxId, activity, "Netting process complete", EnumStatus.Completed);
                    var completeLog = _redisLog.LogToRedis(msgDto, logKey, EnumStatus.Completed);
                    await Task.WhenAll(completeMsg, completeLog);
                    return;
                }
            }
            catch (Exception ex)
            {
                var msg = $"Netting process failed because: {ex}";
                await SendMessage(trxId, activity, msg, EnumStatus.Failed);
                _logger.LogError(ex, "Error occured on trxId : {id} and step : {step} with error message : {ex}", msgDto.TrxId, msgDto.Activity, ex.ToString());
            }
        }

        private async Task SendMessage(Guid trxId, string activity, string msg, EnumStatus status)
        {
            var topic = _config.GetValue<string>("Topic:NettingPartTwoCompleted");
            var dto = new BaseMessageDto()
            {
                TrxId = trxId,
                Activity = activity,//"NettingPartTwoStatusChecker",
                Message = msg,
                Status = status,
                Timestamp = DateTime.UtcNow
            };
            await _sender.SendAsync(topic, dto);
        }
    }
}
