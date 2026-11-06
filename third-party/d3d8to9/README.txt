d3d8.dll тут - обгортка d3d8to9 (Patrick Mours / crosire, ліцензія BSD 2-clause, див. LICENSE.md): перекладає виклики Direct3D 8 у Direct3D 9.
Джерело: https://github.com/crosire/d3d8to9 , версія 1.16.0.
Зміна для NeoBar: прибрано вікно-пропозицію встановити DirectX End-User Runtime (d3dx9_43.dll); без нього гра просто продовжує.
NeoBar копіює цей файл у теку DirectX 8-гри за вашим вибором (налаштування ігор) і видаляє його за вашим наказом.
