using UnityEngine;

// gameObject must have a Light component attached to it
[RequireComponent(typeof(Light))]
public class LightIntensityRandomRange : MonoBehaviour
{

    // declaring the minimum and maximum values of the Light to oscilate randomly between
    public float minIntensity = 0.25f;
    public float maxIntensity = 0.5f;
    // declaring the random variable
    float random;

    // Use this for initialization
    void Start()
    {
        // giving "random" variable a Random range
        random = Random.Range(0.0f, 65535.0f);
    }

    // Update is called once per frame
    void Update()
    {
        // calculating a random value over time
        float noise = Mathf.PerlinNoise(random, Time.time);
        // assigning that random value to the Light component (between the minimum and maximum values, using the random "noise")
        GetComponent<Light>().intensity = Mathf.Lerp(minIntensity, maxIntensity, noise);
    }
}
