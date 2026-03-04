# Anilist List Converter
A simple Tool to Convert Anilist Lists from Manga to Anime or Anime to Manga. Written in C# & XAML.
</br>
Info: This tool only Converts Entries that have the status "Planned" and are on the default lists.

https://github.com/user-attachments/assets/96d09c9d-d667-4507-81ac-ef6fa8ecf126

### Reworked
This tool just got reworked from the Ground up, no more copy pasting Tokens, no more Ratelimit fails.
</br>
Since the new [AnilistNet Package](https://github.com/dentolos19/AniListNet/) has not been published to Nuget i included the DLL in the Project.

### How it works
When it opens click the Authorize Button, now Anilist opens where you need to Authorize the app.
</br>
After that you simply select if you want to convert Anime to Manga or Manga to Anime
</br>
Now you see your list get converted, with a modern progress bar and a Log Textbox for detailed insight.
</br>
Should the App not be able to find e.g. an Manga in Anime form, it will simply get deleted from the Manga List.
