using System.Threading.Tasks;

namespace SampleChecker.BLL.Kafka
{
    public interface IKafkaSender
    {
        Task SendAsync(string topic, object message);
    }
}
