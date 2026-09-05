using UnityEngine;

public class HousePortal : MonoBehaviour
{
    [SerializeField] private GameObject viewToShow;
    [SerializeField] private GameObject viewToHide;


    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.CompareTag("Player"))
            return;


        viewToHide.SetActive(false);
        viewToShow.SetActive(true);
    }
}