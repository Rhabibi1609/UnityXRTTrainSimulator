using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;

public class scr_HandStateController : MonoBehaviour
{

    [SerializeField] private XRRayInteractor TeleportInteractor;
    [SerializeField] private ActionBasedController TeleportControllerActionBased;
    [SerializeField] private XRRayInteractor MainInteractor;
    [SerializeField] private InputActionReference TeleportInputActionReference;

    private void OnEnable()
    {
        TeleportInputActionReference.action.performed += TeleportModeActivate;
        TeleportInputActionReference.action.canceled += TeleportModeCancel;
    }



    private void TeleportModeActivate(InputAction.CallbackContext obj)
    {
        if (MainInteractor.hasSelection) { return; }

        TeleportInteractor.enabled = true;
        TeleportControllerActionBased.enableInputActions = true;

        MainInteractor.enabled = false;
    }

    private void TeleportModeCancel(InputAction.CallbackContext obj) => Invoke("TeleportCancel", 0.1f);
    private void TeleportCancel()
    {
        TeleportInteractor.enabled = false;
        TeleportControllerActionBased.enableInputActions = false;

        MainInteractor.enabled = true;
    }



    private void OnDisable()
    {
        TeleportInputActionReference.action.performed -= TeleportModeActivate;
        TeleportInputActionReference.action.canceled -= TeleportModeCancel;
    }
}
