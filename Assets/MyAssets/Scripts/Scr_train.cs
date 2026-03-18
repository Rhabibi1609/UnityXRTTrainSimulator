using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TMPro;
using System;

public class Scr_train : MonoBehaviour
{

    public GameObject train; // the train object to use
    public TextMeshProUGUI timer; // the timer text to use
    public Renderer hazard_indicator_renderer; // the renderer of the hazard indicator to show when train is passing

    public float speed = 100f; // how far the train moves per second
    public float time_wait = 10f; // how many seconds to wait between trains

    public float train_length = 16.5f*5; // how long the train is
    public float track_length = 100f; // how long the track is
    public float buffer = 5f; // how much space to leave between the train and the edge of the track

    public float hazard_pulse_length = 1f; // how many seconds the pulse of the hazard indicator should last
    public float hazard_pulse_intensity = 0.5f; // the alpha value of the hazard indicator at the peak of a pulse (out of 1f)



    private float time_until_next = 0f; // how many frames until the train will arive
    private bool moving = false; // whether the train is currently moving or not

    private float hazard_pulse_timer = 0f; // how many frames into a hazard pulse it is
    private Color hazard_color = new Color(1f, 1f, 1f, 0f); // the current color of the hazard indicator

    void Start()
    {
        TrainArrive();

        hazard_color = hazard_indicator_renderer.material.color;
        hazard_color.a = 0f;
    }



    void Update()
    {
        if (moving) {
            train.transform.position += new Vector3(speed * Time.deltaTime, 0f, 0f);
            CheckTrainLeft();

            if (hazard_pulse_timer <= 0) { hazard_pulse_timer = hazard_pulse_length; }
        }

        else
        {
            time_until_next -= Time.deltaTime;

            if (time_until_next <= 0f) {
                TrainArrive();
            }
        }
        
        timer.text = "Next train: " + (Math.Ceiling(time_until_next)).ToString() + "s";
        

        hazard_pulse_timer = Math.Clamp(hazard_pulse_timer - Time.deltaTime, 0f, hazard_pulse_length);

        if (hazard_pulse_length > 0)
        {
            hazard_color.a = Math.Clamp(MathMapPulse(hazard_pulse_timer / hazard_pulse_length) * hazard_pulse_intensity, 0f, 1f);
        }
        else
        {
            hazard_color.a = Math.Clamp(hazard_pulse_intensity, 0f, 1f);
        }
        hazard_indicator_renderer.material.color = hazard_color;
    }



    void TrainArrive() // place train before entrace
    {
        train.transform.position = transform.position - new Vector3(train_length + buffer, 0f, 0f);
        moving = true;
    }



    bool CheckTrainLeft() // checks if train is past the exit and if so scedules the next one
    {
        bool train_left = train.transform.position.x >= transform.position.x + track_length + buffer;

        if (train_left)
        {
            moving = false;
            time_until_next = time_wait;
        }



        return train_left;
    }

    float MathMapPulse(float value) // maps a value from 0 to 1 to a pulse shape that goes from 0 to 1 and back to 0 in a smooth way
    {         
        return (float)(Math.Cos((value*2 + 1) * Math.PI) + 1) / 2;
    }

}
