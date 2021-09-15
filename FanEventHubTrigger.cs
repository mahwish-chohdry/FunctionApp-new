using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.EventHubs;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Xavor.Function
{
    public static class FanEventHubTrigger
    {
        #region properties

        private static string baseUrl = "https://apiappterraform.azurewebsites.net";
        private static string connectionString = "Server=tcp:sqlserverterraform.database.windows.net,1433;Initial Catalog=SQLDBterraform;Persist Security Info=False;User ID=Mahwish;Password=Banana1234567;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";
        private static string notificationhubConnectionstring = "Endpoint=sb://nhub-terraform.servicebus.windows.net/;SharedAccessKeyName=DefaultFullSharedAccessSignature;SharedAccessKey=hRT+Eh116NwQ0zsQ9/IoUpe2KExnRx91mQeEhOK/HnQ="; //notificationhub connection string
        private static string notificationhubName = "Hub-Terraform";

        private static string setStatus = baseUrl + "/api/SmartDeviceStatus/SetDeviceStatus";
        private static string setAlarms = baseUrl + "/api/SmartDeviceStatus/SetDeviceAlarms";
        private static string sendAcknowledgement = baseUrl + "/api/Device/SendAcknowledgement/";
        private static string sendDeviceState = baseUrl + "/api/Device/SendDeviceState/";
        private static string speedCommand = baseUrl + "/api/Device/ChangeDeviceSpeed/";
        private static string powerCommand = baseUrl + "/api/Device/ChangeDevicePowerStatus/";

        #endregion

        [FunctionName("FanEventHubTrigger")]
        public static async Task Run([EventHubTrigger("instanceterraform", Connection = "Connectionstring")] EventData[] events, ILogger log)
        {
            var exceptions = new List<Exception>();

            foreach (EventData eventData in events)
            {
                try
                {
                    string messageBody = Encoding.UTF8.GetString(eventData.Body.Array, eventData.Body.Offset, eventData.Body.Count);
                    dynamic input = JsonConvert.DeserializeObject(messageBody);
                    log.LogInformation(messageBody);
                    input["id"].Value = Guid.NewGuid().ToString();
                    if (string.IsNullOrEmpty(input["id"].Value) || string.IsNullOrEmpty(input["DeviceId"].Value))
                    {
                        continue;
                    }

                    using (var connection = new SqlConnection(connectionString))
                    {
                        try
                        {
                            await connection.OpenAsync();
                            log.LogInformation("DB Connected");
                            var messageType = Convert.ToInt32(input["MessageType"].Value);
                            switch (messageType)
                            {
                                case 0: // PLC data
                                    log.LogInformation("Device Alarm Scenario");
                                    DeviceAlarm(input, messageBody, connection, log);
                                    break;
                                case 1: // Sensor data
                                    log.LogInformation("Device Status Scenario");
                                    await DeviceStatus(input, messageBody, connection, log);
                                    break;
                                case 2:
                                    log.LogInformation("Acknowledgement Scenario");
                                    Acknowledgement(input, log);
                                    break;
                                case 3:
                                    log.LogInformation("Device State Scenario");
                                    DeviceState(input, log);
                                    break;
                                case 4:
                                    log.LogInformation("Device Auto Command Scenario");
                                    DeviceAutoCommand(input, log);
                                    break;
                            }
                        }
                        catch (Exception e)
                        {
                            log.LogInformation(e.ToString());
                        }
                        finally
                        {
                            connection.Close();
                            connection.Dispose();
                        }
                    }
                }
                catch (Exception e)
                {
                    // We need to keep processing the rest of the batch - capture this exception and continue.
                    // Also, consider capturing details of the message that failed processing so it can be processed again later.
                    //exceptions.Add(e);
                    log.LogInformation(e.ToString());
                }
            }

            // Once processing of the batch is complete, if any messages in the batch failed processing throw an exception so that there is a record of the failure.

            if (exceptions.Count > 1)
                throw new AggregateException(exceptions);

            if (exceptions.Count == 1)
                throw exceptions.Single();
        }

        #region Message Functions

        /// <summary>
        /// Device Alarm
        /// </summary>
        /// <param name="input"></param>
        /// <param name="data"></param>
        /// <param name="connection"></param>
        /// <param name="log"></param>
        public static void DeviceAlarm(dynamic input, string data, SqlConnection connection, ILogger log)
        {
            var response = RestAPICall(data, setAlarms, log);
            log.LogInformation("Response for DeviceAlarm: " + response);
            var title = "";
            if (input["Alarm"].Value.ToString() != "No Alarm")
            {
                title = "Alarm";
            }
            else if (input["Warning"].Value.ToString() != "No Warning")
            {
                title = "Warning";
            }
            else
            {
                return;
            }

            if (response != "Failure")
            {
                var DeviceUniqueID = input["DeviceId"].Value.ToString();
                var DeviceID = (int)GetDeviceId(input["DeviceId"].Value.ToString(), connection);
                var DeviceName = (string)GetDeviceName(input["DeviceId"].Value.ToString(), connection);
                var message = "{\"message\":\"" + title + " has Occurred in " + DeviceName + "\",\"statusCode\":\"SUCCESS\",\"data\":{\"deviceId\":\"" + DeviceUniqueID + "\",\"isDeviceStatus\":false}}";
                var userMapping = GetUserMappingList(DeviceID, connection);
                var userIdList = GetUserIdList(userMapping);
                var user = GetUserList(userIdList, connection);
                SendPushMessage(message, user, log, false, title + " has Occurred in " + DeviceUniqueID);
            }
        }

        /// <summary>
        /// Device Status
        /// </summary>
        /// <param name="input"></param>
        /// <param name="data"></param>
        /// <param name="connection"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        public static async Task DeviceStatus(dynamic input, string data, SqlConnection connection, ILogger log)
        {
            var response = RestAPICall(data, setStatus, log);
            log.LogInformation("Response for DeviceStatus: " + response);
            if (response != "Failure")
            {
                var device = input["DeviceId"].Value;
                var DeviceID = (int)GetDeviceId(device, connection);
                var userMapping = GetUserMappingList(DeviceID, connection);
                var userIdList = GetUserIdList(userMapping);
                var user = GetUserList(userIdList, connection);

                log.LogInformation("Status is pushed to following user: " + user[0]);
                await SendPushMessage(response, user, log);
            }
        }

        /// <summary>
        /// Acknowledgement
        /// </summary>
        /// <param name="input"></param>
        /// <param name="log"></param>
        public static void Acknowledgement(dynamic input, ILogger log)
        {
            var CommandID = input["CommandId"].Value.ToString();
            var Url = sendAcknowledgement + CommandID;
            string response = RestAPICall("", Url, log);
            log.LogInformation("Response for Acknowledgement: " + response);
        }

        /// <summary>
        /// Device State
        /// </summary>
        /// <param name="input"></param>
        /// <param name="log"></param>
        public static void DeviceState(dynamic input, ILogger log)
        {
            var DeviceId = input["DeviceId"].Value.ToString();
            var CustomerID = input["CustomerId"].Value.ToString();
            string Url = sendDeviceState + CustomerID + '/' + DeviceId;
            var response = RestAPICall("", Url, log);
            log.LogInformation("Response for DeviceState: " + response);
        }

        /// <summary>
        /// Device Auto Command
        /// </summary>
        /// <param name="input"></param>
        /// <param name="log"></param>
        public static void DeviceAutoCommand(dynamic input, ILogger log)
        {
            var autoFlag = input["auto_flag"].Value.ToString();
            var customerId = input["CustomerId"].Value.ToString();
            var deviceId = input["DeviceId"].Value.ToString();
            var speed = input["speed"].Value.ToString();
            var power = input["power"].Value.ToString();
            var Uri = "";

            if (autoFlag == "1")
            {
                Uri = speedCommand + customerId + "/" + deviceId + "/" + speed;
            }
            else if (autoFlag == "0")
            {
                Uri = powerCommand + customerId + "/" + deviceId + "/" + power;
            }
            var response = RestAPICall("", Uri, log);
            log.LogInformation("Response for DeviceAutoCommand: " + response);
        }

        #endregion

        #region private functions

        private static string RestAPICall(string Data, string URL, ILogger log)
        {
            var request = (HttpWebRequest)WebRequest.Create(URL);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = Data.Length;
            using (var webStream = request.GetRequestStream())
            using (var requestWriter = new StreamWriter(webStream, System.Text.Encoding.ASCII))
            {
                requestWriter.Write(Data);
            }
            try
            {
                var webResponse = request.GetResponse();
                using (var webStream = webResponse.GetResponseStream() ?? Stream.Null)
                using (var responseReader = new StreamReader(webStream))
                {
                    string response = responseReader.ReadToEnd();
                    Console.Out.WriteLine(response);
                    return response;
                }
            }
            catch (Exception e)
            {
                log.LogInformation(e.ToString());
                return "Failure";
            }
        }

        private static async Task SendPushMessage(string input, List<string> Tags, ILogger log, bool isStatus = true, string AppleAlert = "")
        {
            var ConnectionString = notificationhubConnectionstring;
            string HubName = notificationhubName;
            var message = Newtonsoft.Json.JsonConvert.SerializeObject(input);
            var _hubClient = NotificationHubClient.CreateClientFromConnectionString(ConnectionString, HubName);
            foreach (var obj in Tags)
            {
                var Content = "{\"data\":{\"message\": " + input + "}}";
                var outcome = await _hubClient.SendFcmNativeNotificationAsync(Content, obj);
                string AppleNotificationContent = "";
                if (isStatus == true)
                {
                    AppleNotificationContent = "{\"aps\":{\"alert\":\"DeviceStatus\", \"alert2\":" + input + "}}";
                }
                else
                {
                    AppleNotificationContent = "{\"aps\":{\"alert\":\"" + AppleAlert + "\", \"alert2\":" + input + "}}";
                }
                var outcome2 = await _hubClient.SendAppleNativeNotificationAsync(AppleNotificationContent, obj);
                log.LogInformation("Following notification is pushed: " + Content + " To Following user: " + obj);
            }
        }

        private static List<int> GetUserIdList(SqlDataReader userdata)
        {
            var userList = new List<int>();
            while (userdata.Read())
            {
                userList.Add((int)userdata["userId"]);
            }
            userdata.Close();
            return userList;
        }

        private static List<string> GetUserList(List<int> userdata, SqlConnection connection)
        {

            var userList = new List<string>();
            foreach (var obj in userdata)
            {
                string query = "SELECT UserID from [User] WHERE id = @Id";
                var cmd = new SqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@Id", obj);
                var result = cmd.ExecuteReader();
                while (result.Read())
                {
                    Console.WriteLine(String.Format("{0}", result[0]));
                    userList.Add(result[0].ToString());
                }
                result.Close();
            }
            return userList;
        }

        private static SqlDataReader GetUserMappingList(int DeviceId, SqlConnection connection)
        {
            string query = "SELECT UserId from dbo.userdevice WHERE DeviceId = @DeviceId";
            var cmd = new SqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@DeviceId", DeviceId);
            var result = cmd.ExecuteReader();
            return result;
        }

        private static object GetDeviceName(string DeviceId, SqlConnection connection)
        {
            string query = "SELECT Name from dbo.device WHERE DeviceID = @DeviceId";
            var cmd = new SqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@DeviceId", DeviceId);
            object result = null;
            using (var data = cmd.ExecuteReader())
            {
                while (data.Read())
                {
                    Console.WriteLine(String.Format("{0}", data[0]));
                    result = data[0];
                }
                data.Close();
            }
            return result;
        }

        private static object GetDeviceId(string DeviceId, SqlConnection connection)
        {
            string query = "SELECT id from dbo.device WHERE DeviceID = @DeviceId";
            var cmd = new SqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@DeviceId", DeviceId);
            object result = null;
            using (var data = cmd.ExecuteReader())
            {
                while (data.Read())
                {
                    Console.WriteLine(String.Format("{0}", data[0]));
                    result = data[0];
                }
                data.Close();
            }
            return result;
        }
              
        private static string GetCommaSeparatedIds(List<int> data)
        {
            var idList = "";
            foreach (var id in data)
            {
                if (string.IsNullOrEmpty(idList))
                    idList = "" + id + "";
                else
                    idList = idList + "," + id + "";
            }
            return idList;
        }

        #endregion
    }
}
