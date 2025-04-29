using MQTT_Automations;
using MQTTnet;

var mqttFactory = new MqttFactory();
var topics = new List<string>()
{
    "SpotifyCurrentlyPlaying/Add",
    "SpotifyControl",
    "PlaySpotifyInKitchen",
    "PopulateSpotifyDownloadPlaylist",
    "Backup/CopyToDropbox"
};

using (var mqttClient = mqttFactory.CreateMqttClient())
{
    var application = new Application(mqttClient, mqttFactory);
    await application.LoginToSpotify();
    await application.Connect(topics);

    Console.WriteLine("Press enter to exit.");
    Console.ReadLine();
}