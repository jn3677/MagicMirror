using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;

public class KinectTest : MonoBehaviour
{
    void Start()
    {
        Debug.Log("Testing Azure Kinect Sensor SDK...");

        try
        {
            int count = Device.GetInstalledCount();

            Debug.Log($"Azure Kinect devices detected: {count}");
            Debug.Log("SUCCESS: Unity -> Azure Kinect Sensor SDK works.");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Azure Kinect test failed:\n{ex}");
        }
    }
}
