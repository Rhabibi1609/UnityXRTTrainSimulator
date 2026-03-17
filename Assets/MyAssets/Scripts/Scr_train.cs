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
    public float speed = 100f; // how far the train moves per second
    public float time_wait = 10f; // how many seconds to wait between trains

    public float train_length = 16.5f*5; // how long the train is
    public float track_length = 100f; // how long the track is
    public float buffer = 5f; // how much space to leave between the train and the edge of the track


    private float time_until_next = 0f; // how many frames until the train will arive
    private bool moving = false; // whether the train is currently moving or not

    void Start()
    {
        TrainArrive();
    }



    void Update()
    {
        if (moving) {
            train.transform.position += new Vector3(speed * Time.deltaTime, 0f, 0f);
            CheckTrainLeft();
        } else
        {
            time_until_next -= Time.deltaTime;

            if (time_until_next <= 0f) {
                TrainArrive();
            }
        }
        
        timer.text = "Next train: " + (Math.Ceiling(time_until_next)).ToString() + "s";
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
}
