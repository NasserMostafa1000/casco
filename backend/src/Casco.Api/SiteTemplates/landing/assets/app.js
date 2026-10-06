document.addEventListener('DOMContentLoaded', () => {
  const btn = document.getElementById('menuBtn'), menu = document.getElementById('mobileMenu');
  const toggle = (open) => { menu.classList.toggle('hidden', !open); menu.classList.toggle('flex', open); btn.setAttribute('aria-expanded', open); };
  btn.addEventListener('click', () => toggle(menu.classList.contains('hidden')));
  menu.querySelectorAll('a').forEach((a) => a.addEventListener('click', () => toggle(false)));
});
